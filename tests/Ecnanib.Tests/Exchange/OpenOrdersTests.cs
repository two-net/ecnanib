using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// One symbol's open orders, which reconciliation reads once a pass (FR-EXE-06): Spot's <c>openOrders</c> with an OCO
/// list's members among them, and on Futures the ordinary and the conditional orders together, each at the weight
/// Binance charges with a symbol (EXT-BIN-04).
/// </summary>
public sealed class OpenOrdersTests(PostgresFixture postgres)
{
    private const string SpotPath = "/api/v3/openOrders";
    private const string FuturesPath = "/fapi/v1/openOrders";
    private const string AlgoPath = "/fapi/v1/openAlgoOrders";

    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task Spot_lists_an_OCO_lists_members_as_orders_and_leaves_out_other_symbols()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, SpotPath, _ => FakeBinance.Json($"[{SpotOrder("target", "LIMIT_MAKER", "60400", "0")},{SpotOrder("stop", "STOP_LOSS", "0", "59800")},{SpotOrder("other", "LIMIT", "1", "0", "ETHUSDT")}]"));

        var orders = await exchange.Account.GetOpenOrdersAsync(Market.Spot, ExchangeHarness.HmacKey(), "BTCUSDT", RequestPriority.Normal, Cancellation);

        Assert.Equal(
            [("target", OrderType.Limit, 60_400m, (decimal?)null), ("stop", OrderType.StopMarket, (decimal?)null, 59_800m)],
            orders.Select(order => (order.ClientOrderId, order.Type!.Value, order.Price, order.StopPrice)));
        Assert.All(orders, order => Assert.Equal((ExchangeOrderStatus.New, OrderSide.Sell, 0.01664m), (order.Status, order.Side, order.Quantity)));
        var request = Assert.Single(exchange.Binance.To(SpotPath));
        Assert.Equal((FakeBinance.SpotDemo, "BTCUSDT"), (request.Host, request.Parameter("symbol")));
        Assert.NotNull(request.Parameter("signature"));
    }

    [Fact]
    public async Task Futures_lists_ordinary_and_conditional_orders_together_by_their_client_IDs()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, FuturesPath, _ => FakeBinance.Json($"[{FuturesOrder("entry")}]"));
        exchange.Binance.On(HttpMethod.Get, AlgoPath, _ => FakeBinance.Json($"[{AlgoOrder("stop", "STOP_MARKET", "59800")},{AlgoOrder("target", "TAKE_PROFIT_MARKET", "60400")}]"));

        var orders = await exchange.Account.GetOpenOrdersAsync(Market.Futures, ExchangeHarness.HmacKey(TradingEnvironment.Live), "BTCUSDT", RequestPriority.Normal, Cancellation);

        Assert.Equal(
            [("entry", OrderType.Limit, (decimal?)null), ("stop", OrderType.StopMarket, 59_800m), ("target", OrderType.TakeProfitMarket, 60_400m)],
            orders.Select(order => (order.ClientOrderId, order.Type!.Value, order.StopPrice)));
        Assert.All(orders, order => Assert.Equal(ExchangeOrderStatus.New, order.Status));
        Assert.Equal(
            [(FakeBinance.FuturesLive, "BTCUSDT"), (FakeBinance.FuturesLive, "BTCUSDT")],
            exchange.Binance.To(FuturesPath).Concat(exchange.Binance.To(AlgoPath)).Select(request => (request.Host, request.Parameter("symbol"))));
    }

    [Fact]
    public async Task Futures_fails_when_either_list_fails_so_nothing_is_taken_as_gone()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, FuturesPath, _ => FakeBinance.Json("[]"));
        exchange.Binance.On(HttpMethod.Get, AlgoPath, _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Account.GetOpenOrdersAsync(Market.Futures, ExchangeHarness.HmacKey(), "BTCUSDT", RequestPriority.Normal, Cancellation));

        await Assert.ThrowsAsync<ArgumentException>(
            () => exchange.Account.GetOpenOrdersAsync(Market.Futures, ExchangeHarness.HmacKey(), "btc", RequestPriority.Normal, Cancellation));
    }

    [Theory]
    [InlineData(Market.Spot, 6)]
    [InlineData(Market.Futures, 2)]
    public async Task Each_read_reserves_the_weight_Binance_charges_with_a_symbol(Market market, int weight)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.StartOfMinute();
        var release = new TaskCompletionSource();
        var arrived = 0;
        var all = new TaskCompletionSource();

        foreach (var path in market == Market.Spot ? [SpotPath] : new[] { FuturesPath, AlgoPath })
        {
            exchange.Binance.On(HttpMethod.Get, path, async (_, _) =>
            {
                if (Interlocked.Increment(ref arrived) == (market == Market.Spot ? 1 : 2))
                {
                    all.SetResult();
                }

                await release.Task;

                return FakeBinance.Json("[]");
            });
        }

        var call = exchange.Account.GetOpenOrdersAsync(market, ExchangeHarness.HmacKey(), "BTCUSDT", RequestPriority.Normal, Cancellation);
        await all.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);

        // A signed read syncs the clock first, whose answer reports a weight of 1.
        Assert.Equal(1 + weight, exchange.Usage(market == Market.Spot ? ExchangeServer.SpotDemo : ExchangeServer.FuturesDemo).UsedWeight);

        release.SetResult();
        Assert.Empty(await call);
    }

    private static string SpotOrder(string clientOrderId, string type, string price, string stopPrice, string symbol = "BTCUSDT") =>
        $$"""
        {"symbol":"{{symbol}}","orderId":{{Math.Abs(clientOrderId.GetHashCode(StringComparison.Ordinal)) % 100000}},"orderListId":7,"clientOrderId":"{{clientOrderId}}",
         "price":"{{price}}","origQty":"0.01664","executedQty":"0","cummulativeQuoteQty":"0","status":"NEW","timeInForce":"GTC",
         "type":"{{type}}","side":"SELL","stopPrice":"{{stopPrice}}","icebergQty":"0","time":1762430000000,"updateTime":1762430000000,
         "isWorking":false,"workingTime":-1,"origQuoteOrderQty":"0","selfTradePreventionMode":"EXPIRE_MAKER"}
        """;

    private static string FuturesOrder(string clientOrderId) =>
        $$"""
        {"avgPrice":"0","clientOrderId":"{{clientOrderId}}","cumQuote":"0","executedQty":"0","orderId":4151,"origQty":"0.01","origType":"LIMIT",
         "price":"59900","reduceOnly":false,"side":"BUY","positionSide":"BOTH","status":"NEW","stopPrice":"0","closePosition":false,
         "symbol":"BTCUSDT","timeInForce":"GTC","type":"LIMIT","updateTime":1762430000000}
        """;

    private static string AlgoOrder(string clientAlgoId, string type, string trigger) =>
        $$"""
        {"algoId":{{(type == "STOP_MARKET" ? 1 : 2)}},"clientAlgoId":"{{clientAlgoId}}","algoType":"CONDITIONAL","orderType":"{{type}}","symbol":"BTCUSDT",
         "side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.01","algoStatus":"NEW","actualOrderId":"","actualPrice":"0",
         "triggerPrice":"{{trigger}}","price":"0","workingType":"MARK_PRICE","reduceOnly":true,"createTime":1762430000000,"updateTime":1762430000000}
        """;
}
