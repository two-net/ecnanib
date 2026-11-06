using System.Diagnostics;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// What order execution (#34) needs of the adapter: Spot OCO lists (FR-EXE-02), an order's trades for what a Spot buy
/// left in the account (FR-AGT-09), the reasons it acts on, and protective orders that settle sooner (FR-EXE-05,
/// FR-EXE-09).
/// </summary>
public sealed class ProtectiveOrderTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    private static OrderListRequest Oco() =>
        new("BTCUSDT", OrderSide.Sell, 0.001m, 66000m, 58000m, "list-1", "target-1", "stop-1", RequestPriority.Protective);

    [Fact]
    public async Task A_Spot_OCO_list_closes_a_long_at_its_target_above_or_its_stop_below()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/api/v3/orderList/oco", request => FakeBinance.Json(BinanceSamples.OrderList(
            request.Parameter("listClientOrderId")!, request.Parameter("aboveClientOrderId")!, request.Parameter("belowClientOrderId")!)));

        var list = await exchange.Trading.PlaceOrderListAsync(ExchangeHarness.HmacKey(), Oco(), Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/orderList/oco"));
        Assert.Equal(FakeBinance.SpotDemo, request.Host);
        Assert.Equal(
            [
                ("symbol", "BTCUSDT"), ("listClientOrderId", "list-1"), ("side", "SELL"), ("quantity", "0.001"),
                ("aboveType", "LIMIT_MAKER"), ("aboveClientOrderId", "target-1"), ("abovePrice", "66000"),
                ("belowType", "STOP_LOSS"), ("belowClientOrderId", "stop-1"), ("belowStopPrice", "58000"),
                ("newOrderRespType", "RESULT"),
            ],
            request.Parameters.Where(parameter => parameter.Name is not ("timestamp" or "recvWindow" or "signature")));
        Assert.Equal("2000", request.Parameter("recvWindow"));

        Assert.Equal((1929L, "list-1", OrderListStatus.Executing), (list.OrderListId, list.ListClientOrderId, list.Status));
        Assert.Equal([new OrderListMember("target-1", 31), new OrderListMember("stop-1", 32)], list.Orders);
    }

    [Fact]
    public async Task An_OCO_list_needs_its_target_and_stop_on_the_right_sides_and_IDs_of_their_own()
    {
        Assert.Throws<ArgumentException>(() => new OrderListRequest(
            "BTCUSDT", OrderSide.Sell, 0.001m, 58000m, 66000m, "list-1", "target-1", "stop-1", RequestPriority.Protective));
        Assert.Throws<ArgumentException>(() => new OrderListRequest(
            "BTCUSDT", OrderSide.Sell, 0.001m, 66000m, 58000m, "list-1", "same", "same", RequestPriority.Protective));

        // Closing a short buys the other way round.
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/api/v3/orderList/oco", request => FakeBinance.Json(BinanceSamples.OrderList("list-2", "t", "s")));

        await exchange.Trading.PlaceOrderListAsync(
            ExchangeHarness.HmacKey(),
            new OrderListRequest("BTCUSDT", OrderSide.Buy, 0.001m, 58000m, 66000m, "list-2", "target-2", "stop-2", RequestPriority.Protective),
            Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/orderList/oco"));
        Assert.Equal(("STOP_LOSS", "66000", "LIMIT_MAKER", "58000"), (
            request.Parameter("aboveType"), request.Parameter("aboveStopPrice"), request.Parameter("belowType"), request.Parameter("belowPrice")));
    }

    [Fact]
    public async Task An_OCO_list_with_an_unknown_outcome_and_its_orders_are_not_said_to_be_missing_until_it_settles()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        exchange.Binance.On(HttpMethod.Post, "/api/v3/orderList/oco", _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));
        exchange.Binance.On(HttpMethod.Get, "/api/v3/orderList", _ => FakeBinance.Error(HttpStatusCode.BadRequest, -2013, "Order list does not exist."));
        exchange.Binance.On(HttpMethod.Get, "/api/v3/order", _ => FakeBinance.Error(HttpStatusCode.BadRequest, -2013, "Order does not exist."));

        var unknown = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderListAsync(key, Oco(), Cancellation));

        // A protective order's receive window, 2 seconds, and a margin of 5 more.
        var settlesAt = unknown.SettlesAt!.Value;
        Assert.Equal(ExchangeFailure.Unknown, unknown.Failure);
        Assert.InRange((settlesAt - exchange.Clock.GetUtcNow()).TotalSeconds, 6, 7);

        var list = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Account.GetOrderListAsync(key, Oco().Reference, RequestPriority.Protective, Cancellation));
        var stop = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetOrderAsync(
            Market.Spot, key, new OrderReference("BTCUSDT", "stop-1", OrderType.StopMarket), RequestPriority.Protective, Cancellation));
        Assert.Equal((ExchangeFailure.Unknown, ExchangeFailure.Unknown), (list.Failure, stop.Failure));

        exchange.Clock.Advance(settlesAt - exchange.Clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
        Assert.Null(await exchange.Account.GetOrderListAsync(key, Oco().Reference, RequestPriority.Protective, Cancellation));
        Assert.Equal("list-1", exchange.Binance.To("/api/v3/orderList").Last().Parameter("origClientOrderId"));
    }

    [Fact]
    public async Task An_OCO_list_is_cancelled_by_its_client_order_ID()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Delete, "/api/v3/orderList", _ => FakeBinance.Json(BinanceSamples.OrderList("list-1", "t", "s", "ALL_DONE")));

        await exchange.Trading.CancelOrderListAsync(ExchangeHarness.HmacKey(), Oco().Reference, RequestPriority.Protective, Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/orderList"));
        Assert.Equal((HttpMethod.Delete, "BTCUSDT", "list-1"), (request.Method, request.Parameter("symbol"), request.Parameter("listClientOrderId")));
    }

    [Fact]
    public async Task An_orders_trades_are_read_with_their_commissions()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        exchange.Binance.On(HttpMethod.Get, "/api/v3/myTrades", _ => FakeBinance.Json(BinanceSamples.SpotTrades));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/userTrades", _ => FakeBinance.Json(BinanceSamples.FuturesTrades));

        var spot = await exchange.Account.GetOrderTradesAsync(Market.Spot, key, "BTCUSDT", 28, RequestPriority.Protective, Cancellation);
        var futures = await exchange.Account.GetOrderTradesAsync(Market.Futures, key, "BTCUSDT", 22542179, RequestPriority.Normal, Cancellation);

        Assert.Equal([(0.0006m, 0.0000006m, "BTC", false), (0.0004m, 0.0000004m, "BTC", true)], spot.Select(trade => (trade.Quantity, trade.Commission, trade.CommissionAsset, trade.Maker)));
        Assert.Equal((0.100m, 2.42m, "USDT", 60500.00m), (futures[0].Quantity, futures[0].Commission, futures[0].CommissionAsset, futures[0].Price));
        Assert.Equal("28", exchange.Binance.To("/api/v3/myTrades").Single().Parameter("orderId"));
        Assert.Equal("22542179", exchange.Binance.To("/fapi/v1/userTrades").Single().Parameter("orderId"));
    }

    [Theory]
    [InlineData("/fapi/v1/algoOrder", -2021, "Order would immediately trigger.", OrderRejection.WouldTriggerImmediately)]
    [InlineData("/api/v3/order", -2010, "Order would trigger immediately.", OrderRejection.WouldTriggerImmediately)]
    [InlineData("/api/v3/order", -2010, "Stop price would trigger immediately.", OrderRejection.WouldTriggerImmediately)]
    [InlineData("/api/v3/order", -2010, "Order would immediately match and take.", OrderRejection.WouldMatchImmediately)]
    [InlineData("/api/v3/order", -2010, "Account has insufficient balance for requested action.", OrderRejection.None)]
    public async Task A_stop_the_price_has_passed_is_told_apart_from_other_rejections(string path, int code, string message, OrderRejection expected)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, path, _ => FakeBinance.Error(HttpStatusCode.BadRequest, code, message));
        var market = path.StartsWith("/fapi", StringComparison.Ordinal) ? Market.Futures : Market.Spot;

        var exception = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(
            market,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, 0.1m, "stop-1", RequestPriority.Protective, stopPrice: 58000m),
            Cancellation));

        Assert.Equal((ExchangeFailure.Rejected, code, expected), (exception.Failure, exception.Code, exception.Rejection));
    }

    [Fact]
    public async Task A_protective_order_settles_sooner_than_a_normal_one()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/order", _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));

        var normal = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(
            Market.Futures,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 0.1m, "entry-1", RequestPriority.Normal),
            Cancellation));
        var protective = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(
            Market.Futures,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.Market, 0.1m, "close-1", RequestPriority.Protective, reduceOnly: true),
            Cancellation));

        var now = exchange.Clock.GetUtcNow();
        Assert.InRange((normal.SettlesAt!.Value - now).TotalSeconds, 9, 10);
        Assert.InRange((protective.SettlesAt!.Value - now).TotalSeconds, 6, 7);
        Assert.Equal(["5000", "2000"], exchange.Binance.To("/fapi/v1/order").Select(request => request.Parameter("recvWindow")));
    }

    [Fact]
    public async Task A_protective_order_that_gets_no_answer_is_unknown_after_four_seconds()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/order", async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });
        var started = Stopwatch.GetTimestamp();

        var exception = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.PlaceOrderAsync(
            Market.Futures,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.Market, 0.1m, "close-1", RequestPriority.Protective, reduceOnly: true),
            Cancellation));

        Assert.Equal(ExchangeFailure.Unknown, exception.Failure);
        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 3.5, 9);
    }

    [Fact]
    public async Task An_account_read_a_close_depends_on_may_use_the_headroom()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.StartOfMinute();
        var key = ExchangeHarness.HmacKey();

        // 30% of 6000 is kept back, so normal requests stop at 4200. The account read weighs 20.
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount, headers: ("X-MBX-USED-WEIGHT-1M", "4190")));
        await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);

        await Assert.ThrowsAsync<ExchangeException>(() => exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation));
        await exchange.Account.GetAccountAsync(Market.Spot, key, RequestPriority.Protective, Cancellation);

        Assert.Equal(2, exchange.Binance.To("/api/v3/account").Count);
    }
}
