using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// What order execution (#34) relies on, against Binance's Demo servers at the URLs appsettings.json ships, through the
/// real adapter. They pin what the fakes take from Binance's documentation and its official connectors: the algo order
/// service's parameters and codes, Spot OCO lists, the trades' shape, the rejection codes Execution acts on, and the
/// Futures account endpoints. Each market needs ECNANIB_LIVE_BINANCE_SPOT_KEY and _SECRET, or
/// ECNANIB_LIVE_BINANCE_FUTURES_KEY and _SECRET, for HMAC Demo keys, and is skipped without them, so CI never calls
/// Binance. Orders rest far from the price and are cancelled. A Futures check opens and closes the smallest position,
/// and a Spot one buys about 6 USDT of BTC, with Demo funds.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveBinanceTests : IDisposable
{
    private const string Symbol = "BTCUSDT";

    private readonly ServiceProvider _services = Shipped();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IExchangeTrading Trading => _services.GetRequiredService<IExchangeTrading>();

    private IExchangeAccount Account => _services.GetRequiredService<IExchangeAccount>();

    private IExchangeMarketData MarketData => _services.GetRequiredService<IExchangeMarketData>();

    [Fact]
    public async Task Spot_takes_reads_and_cancels_an_OCO_list_with_a_protective_receive_window()
    {
        var key = Key(Market.Spot);
        var (price, rules) = await PriceAsync(Market.Spot);
        var quantity = Quantity(rules, 6m / price);
        var ids = Ids("oco");

        // Closing a short buys: a stop far above the price, and a maker-only target far below it, so neither fills.
        var list = await Trading.PlaceOrderListAsync(
            key,
            new OrderListRequest(Symbol, OrderSide.Buy, quantity, Tick(rules, price * 0.8m), Tick(rules, price * 1.2m), ids[0], ids[1], ids[2], RequestPriority.Protective),
            Cancellation);

        try
        {
            Assert.Equal(OrderListStatus.Executing, list.Status);
            Assert.Equal(2, list.Orders.Count);

            var read = await Account.GetOrderListAsync(key, new OrderListReference(Symbol, ids[0]), RequestPriority.Protective, Cancellation);
            Assert.Equal(OrderListStatus.Executing, read!.Status);

            var stop = await Account.GetOrderAsync(Market.Spot, key, new OrderReference(Symbol, ids[2], OrderType.StopMarket), RequestPriority.Protective, Cancellation);
            Assert.Equal((ExchangeOrderStatus.New, OrderType.StopMarket), (stop!.Status, stop.Type));
        }
        finally
        {
            await Trading.CancelOrderListAsync(key, new OrderListReference(Symbol, ids[0]), RequestPriority.Protective, Cancellation);
        }

        var cancelled = await Account.GetOrderListAsync(key, new OrderListReference(Symbol, ids[0]), RequestPriority.Normal, Cancellation);
        Assert.Equal(OrderListStatus.AllDone, cancelled!.Status);
    }

    [Fact]
    public async Task Spot_says_an_order_list_that_doesnt_exist_is_missing()
    {
        var key = Key(Market.Spot);

        Assert.Null(await Account.GetOrderListAsync(key, new OrderListReference(Symbol, Ids("none")[0]), RequestPriority.Normal, Cancellation));
        Assert.Null(await Account.GetOrderAsync(Market.Spot, key, new OrderReference(Symbol, Ids("none")[0], OrderType.StopMarket), RequestPriority.Normal, Cancellation));
    }

    [Fact]
    public async Task Spot_rejects_a_stop_the_price_has_passed_and_a_target_it_has_reached_as_Execution_expects()
    {
        var key = Key(Market.Spot);
        var (price, rules) = await PriceAsync(Market.Spot);
        var quantity = Quantity(rules, 6m / price);

        var stop = await Assert.ThrowsAsync<ExchangeException>(() => Trading.PlaceOrderAsync(
            Market.Spot,
            key,
            new OrderRequest(Symbol, OrderSide.Buy, OrderType.StopMarket, quantity, Ids("trig")[0], RequestPriority.Protective, stopPrice: Tick(rules, price * 0.9m)),
            Cancellation));
        Assert.Equal((ExchangeFailure.Rejected, OrderRejection.WouldTriggerImmediately), (stop.Failure, stop.Rejection));

        var ids = Ids("match");
        var target = await Assert.ThrowsAsync<ExchangeException>(() => Trading.PlaceOrderListAsync(
            key,
            new OrderListRequest(Symbol, OrderSide.Buy, quantity, Tick(rules, price * 1.05m), Tick(rules, price * 1.2m), ids[0], ids[1], ids[2], RequestPriority.Protective),
            Cancellation));
        Assert.Equal((ExchangeFailure.Rejected, OrderRejection.WouldMatchImmediately), (target.Failure, target.Rejection));
    }

    [Fact]
    public async Task Spot_reads_a_filled_orders_trades_with_their_commission()
    {
        var key = Key(Market.Spot);
        var (price, rules) = await PriceAsync(Market.Spot);

        var order = await Trading.PlaceOrderAsync(
            Market.Spot, key, new OrderRequest(Symbol, OrderSide.Buy, OrderType.Market, Quantity(rules, 6m / price), Ids("buy")[0], RequestPriority.Normal), Cancellation);
        var trades = await Account.GetOrderTradesAsync(Market.Spot, key, Symbol, order.ExchangeOrderId, RequestPriority.Protective, Cancellation);

        Assert.Equal(ExchangeOrderStatus.Filled, order.Status);
        Assert.Equal(order.ExecutedQuantity, trades.Sum(trade => trade.Quantity));
        Assert.All(trades, trade => Assert.False(string.IsNullOrEmpty(trade.CommissionAsset)));
    }

    [Fact]
    public async Task Spot_names_a_filter_it_rejects_an_order_on()
    {
        var key = Key(Market.Spot);
        var (price, rules) = await PriceAsync(Market.Spot);

        // Half a step more than the grid allows.
        var offGrid = Quantity(rules, 6m / price) + (rules.Quantity!.StepSize / 2);
        var exception = await Assert.ThrowsAsync<ExchangeException>(() => Trading.PlaceOrderAsync(
            Market.Spot, key, new OrderRequest(Symbol, OrderSide.Buy, OrderType.Limit, offGrid, Ids("grid")[0], RequestPriority.Normal, price: Tick(rules, price * 0.5m)), Cancellation));

        Assert.True(exception.IsFilterRejection, $"Code {exception.Code}: {exception.Message}");
    }

    [Fact]
    public async Task Futures_takes_reduce_only_stops_and_targets_on_the_mark_price_through_the_algo_service()
    {
        var key = Key(Market.Futures);
        var (price, rules) = await PriceAsync(Market.Futures);
        var quantity = Quantity(rules, Math.Max(rules.Notional?.Minimum ?? 100m, 100m) * 1.1m / price);
        var ids = Ids("fut");

        Assert.False((await Account.GetFuturesAccountConfigAsync(key, Cancellation)).HedgeMode, "The Demo account must be in One-way Mode.");
        Assert.NotNull(await Account.GetFuturesSymbolConfigAsync(key, Symbol, Cancellation));
        Assert.NotEmpty(await Account.GetLeverageBracketsAsync(key, Symbol, Cancellation));
        await MarketData.GetFundingIntervalsAsync(TradingEnvironment.Demo, Cancellation);

        var entry = await Trading.PlaceOrderAsync(
            Market.Futures, key, new OrderRequest(Symbol, OrderSide.Buy, OrderType.Market, quantity, ids[0], RequestPriority.Normal), Cancellation);

        try
        {
            var stop = await Trading.PlaceOrderAsync(
                Market.Futures,
                key,
                new OrderRequest(Symbol, OrderSide.Sell, OrderType.StopMarket, entry.ExecutedQuantity, ids[1], RequestPriority.Protective, stopPrice: Tick(rules, price * 0.8m), reduceOnly: true),
                Cancellation);
            var target = await Trading.PlaceOrderAsync(
                Market.Futures,
                key,
                new OrderRequest(Symbol, OrderSide.Sell, OrderType.TakeProfitMarket, entry.ExecutedQuantity, ids[2], RequestPriority.Normal, stopPrice: Tick(rules, price * 1.2m), reduceOnly: true),
                Cancellation);

            Assert.Equal((ExchangeOrderStatus.New, ExchangeOrderStatus.New), (stop.Status, target.Status));
            var read = await Account.GetOrderAsync(Market.Futures, key, new OrderReference(Symbol, ids[1], OrderType.StopMarket), RequestPriority.Protective, Cancellation);
            Assert.Equal(ExchangeOrderStatus.New, read!.Status);

            var trades = await Account.GetOrderTradesAsync(Market.Futures, key, Symbol, entry.ExchangeOrderId, RequestPriority.Normal, Cancellation);
            Assert.Equal(entry.ExecutedQuantity, trades.Sum(trade => trade.Quantity));

            // A stop above the mark price for a long would trigger at once.
            var passed = await Assert.ThrowsAsync<ExchangeException>(() => Trading.PlaceOrderAsync(
                Market.Futures,
                key,
                new OrderRequest(Symbol, OrderSide.Sell, OrderType.StopMarket, entry.ExecutedQuantity, ids[3], RequestPriority.Protective, stopPrice: Tick(rules, price * 1.1m), reduceOnly: true),
                Cancellation));
            Assert.Equal(OrderRejection.WouldTriggerImmediately, passed.Rejection);

            await Trading.CancelOrderAsync(Market.Futures, key, new OrderReference(Symbol, ids[1], OrderType.StopMarket), RequestPriority.Protective, Cancellation);
            await Trading.CancelOrderAsync(Market.Futures, key, new OrderReference(Symbol, ids[2], OrderType.TakeProfitMarket), RequestPriority.Protective, Cancellation);
        }
        finally
        {
            await Trading.PlaceOrderAsync(
                Market.Futures,
                key,
                new OrderRequest(Symbol, OrderSide.Sell, OrderType.Market, entry.ExecutedQuantity, ids[4], RequestPriority.Protective, reduceOnly: true),
                Cancellation);
        }
    }

    [Fact]
    public async Task Futures_says_a_conditional_order_that_doesnt_exist_is_missing()
    {
        var key = Key(Market.Futures);

        Assert.Null(await Account.GetOrderAsync(Market.Futures, key, new OrderReference(Symbol, Ids("none")[0], OrderType.StopMarket), RequestPriority.Normal, Cancellation));
        Assert.Null(await Account.GetOrderAsync(Market.Futures, key, new OrderReference(Symbol, Ids("none")[0], OrderType.Market), RequestPriority.Normal, Cancellation));
    }

    public void Dispose() => _services.Dispose();

    private async Task<(decimal Price, SymbolRules Rules)> PriceAsync(Market market)
    {
        var quote = await MarketData.GetQuoteAsync(market, TradingEnvironment.Demo, Symbol, Cancellation);
        var info = await MarketData.GetExchangeInfoAsync(market, TradingEnvironment.Demo, Cancellation);

        return (quote.LastPrice, info.Find(Symbol)!);
    }

    private static decimal Quantity(SymbolRules rules, decimal atLeast)
    {
        var step = rules.Quantity!.StepSize;

        return Math.Max(rules.Quantity.Minimum, Math.Ceiling(atLeast / step) * step);
    }

    private static decimal Tick(SymbolRules rules, decimal price)
    {
        var tick = rules.Price!.TickSize;

        return Math.Floor(price / tick) * tick;
    }

    /// <summary>Fresh client order IDs, so a run never meets one from before.</summary>
    private static string[] Ids(string purpose)
    {
        var run = Guid.NewGuid().ToString("N")[..12];

        return [.. Enumerable.Range(0, 5).Select(index => $"live-{purpose}-{run}-{index}")];
    }

    private static ExchangeKey Key(Market market)
    {
        var prefix = $"ECNANIB_LIVE_BINANCE_{market.ToString().ToUpperInvariant()}";
        var apiKey = Environment.GetEnvironmentVariable(prefix + "_KEY");
        var secret = Environment.GetEnvironmentVariable(prefix + "_SECRET");

        Assert.SkipWhen(string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(secret), $"Set {prefix}_KEY and {prefix}_SECRET to call Binance {market} Demo.");

        return new ExchangeKey(Guid.NewGuid(), TradingEnvironment.Demo, ExchangeKeyType.Hmac, SecretValue.FromString(apiKey!), SecretValue.FromString(secret!));
    }

    private static ServiceProvider Shipped()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IHostEnvironment>(new HostingEnvironment
            {
                EnvironmentName = Environments.Production,
                ApplicationName = "Ecnanib",
                ContentRootPath = AppContext.BaseDirectory,
            })
            .AddExchangeAdapter()
            .BuildServiceProvider();
    }
}
