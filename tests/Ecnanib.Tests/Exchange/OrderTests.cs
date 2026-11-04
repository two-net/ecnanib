using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>The account and order endpoints, and how their answers are read.</summary>
public sealed class OrderTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task A_Spot_limit_order_is_sent_and_read_back()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/api/v3/order", request =>
            FakeBinance.Json(BinanceSamples.SpotOrder(request.Parameter("newClientOrderId")!, status: "PARTIALLY_FILLED")));

        var order = await exchange.Trading.PlaceOrderAsync(
            Market.Spot,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Limit, 0.002m, "a1-c2-entry", RequestPriority.Normal, price: 60000.50m),
            Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/order"));
        Assert.Equal(
            [
                ("symbol", "BTCUSDT"), ("side", "BUY"), ("type", "LIMIT"), ("timeInForce", "GTC"), ("quantity", "0.002"),
                ("price", "60000.50"), ("newClientOrderId", "a1-c2-entry"), ("newOrderRespType", "RESULT"),
            ],
            request.Parameters.Where(parameter => parameter.Name is not ("timestamp" or "recvWindow" or "signature")));
        Assert.Equal(
            new ExchangeOrder(
                "BTCUSDT", "a1-c2-entry", 28, OrderSide.Buy, OrderType.Limit, ExchangeOrderStatus.PartiallyFilled,
                0.002m, 0.001m, 60000m, null, 60500m, null, DateTimeOffset.FromUnixTimeMilliseconds(1507725176595)),
            order);
    }

    [Fact]
    public async Task A_Spot_stop_is_a_STOP_LOSS_order()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/api/v3/order", request =>
            FakeBinance.Json(BinanceSamples.SpotOrder(request.Parameter("newClientOrderId")!, type: "STOP_LOSS")));

        var order = await exchange.Trading.PlaceOrderAsync(
            Market.Spot,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, 0.002m, "a1-c2-stop", RequestPriority.Protective, stopPrice: 58000m),
            Cancellation);

        var request = Assert.Single(exchange.Binance.To("/api/v3/order"));
        Assert.Equal(("STOP_LOSS", "58000", null), (request.Parameter("type"), request.Parameter("stopPrice"), request.Parameter("timeInForce")));
        Assert.Equal(OrderType.StopMarket, order.Type);
    }

    [Fact]
    public async Task A_Futures_stop_goes_to_the_algo_order_service()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/algoOrder", request => FakeBinance.Json(BinanceSamples.AlgoOrder(request.Parameter("clientAlgoId")!)));

        var order = await exchange.Trading.PlaceOrderAsync(
            Market.Futures,
            ExchangeHarness.HmacKey(TradingEnvironment.Live),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, 0.1m, "a1-c2-stop", RequestPriority.Protective, stopPrice: 58000m, reduceOnly: true),
            Cancellation);

        var request = Assert.Single(exchange.Binance.To("/fapi/v1/algoOrder"));
        Assert.Equal(FakeBinance.FuturesLive, request.Host);
        Assert.Equal(
            [
                ("algoType", "CONDITIONAL"), ("symbol", "BTCUSDT"), ("side", "SELL"), ("type", "STOP_MARKET"), ("quantity", "0.1"),
                ("triggerPrice", "58000"), ("reduceOnly", "true"), ("clientAlgoId", "a1-c2-stop"),
            ],
            request.Parameters.Where(parameter => parameter.Name is not ("timestamp" or "recvWindow" or "signature")));
        Assert.Empty(exchange.Binance.To("/fapi/v1/order"));
        Assert.Equal(
            (2146760L, "a1-c2-stop", OrderType.StopMarket, ExchangeOrderStatus.New, 58000m, (long?)null),
            (order.ExchangeOrderId, order.ClientOrderId, order.Type, order.Status, order.StopPrice, order.TriggeredOrderId));
    }

    [Fact]
    public async Task A_Futures_stop_is_read_and_cancelled_through_the_algo_order_service()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        var reference = new OrderReference("BTCUSDT", "a1-c2-stop", OrderType.StopMarket);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/algoOrder", request =>
            FakeBinance.Json(BinanceSamples.AlgoOrder(request.Parameter("clientAlgoId")!, status: "TRIGGERED", actualOrderId: "8886774")));
        exchange.Binance.On(HttpMethod.Delete, "/fapi/v1/algoOrder", _ =>
            FakeBinance.Json("""{"algoId":2146760,"clientAlgoId":"a1-c2-stop","code":"200","msg":"success"}"""));

        var order = await exchange.Account.GetOrderAsync(Market.Futures, key, reference, RequestPriority.Protective, Cancellation);
        await exchange.Trading.CancelOrderAsync(Market.Futures, key, reference, RequestPriority.Protective, Cancellation);

        Assert.Equal((ExchangeOrderStatus.Triggered, 8886774L), (order!.Status, order.TriggeredOrderId));
        Assert.Equal([("clientAlgoId", "a1-c2-stop")], Strip(Assert.Single(exchange.Binance.To("/fapi/v1/algoOrder"), request => request.Method == HttpMethod.Get)));
        Assert.Equal([("clientAlgoId", "a1-c2-stop")], Strip(Assert.Single(exchange.Binance.To("/fapi/v1/algoOrder"), request => request.Method == HttpMethod.Delete)));
    }

    [Fact]
    public async Task A_Futures_market_order_is_read_with_its_average_price()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/order", request => FakeBinance.Json(BinanceSamples.FuturesOrder(request.Parameter("newClientOrderId")!)));

        var order = await exchange.Trading.PlaceOrderAsync(
            Market.Futures,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.Market, 0.1m, "a1-c2-close", RequestPriority.Protective, reduceOnly: true),
            Cancellation);

        Assert.Equal("true", Assert.Single(exchange.Binance.To("/fapi/v1/order")).Parameter("reduceOnly"));
        Assert.Equal(
            (22542179L, ExchangeOrderStatus.Filled, 0.1m, (decimal?)null, (decimal?)60500m),
            (order.ExchangeOrderId, order.Status, order.ExecutedQuantity, order.Price, order.AveragePrice));
    }

    [Fact]
    public async Task Spot_and_Futures_orders_are_queried_and_cancelled_by_client_order_ID()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        var reference = new OrderReference("BTCUSDT", "a1-c2-entry", OrderType.Limit);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/order", _ => FakeBinance.Json(BinanceSamples.SpotOrder("a1-c2-entry")));
        exchange.Binance.On(HttpMethod.Delete, "/api/v3/order", _ => FakeBinance.Json(BinanceSamples.SpotOrder("a1-c2-entry", status: "CANCELED")));
        exchange.Binance.On(HttpMethod.Delete, "/fapi/v1/order", _ => FakeBinance.Json(BinanceSamples.FuturesOrder("a1-c2-entry", status: "CANCELED")));

        await exchange.Account.GetOrderAsync(Market.Spot, key, reference, RequestPriority.Normal, Cancellation);
        await exchange.Trading.CancelOrderAsync(Market.Spot, key, reference, RequestPriority.Normal, Cancellation);
        await exchange.Trading.CancelOrderAsync(Market.Futures, key, reference, RequestPriority.Normal, Cancellation);

        Assert.All(
            exchange.Binance.Requests.Where(request => request.Path is "/api/v3/order" or "/fapi/v1/order"),
            request => Assert.Equal([("symbol", "BTCUSDT"), ("origClientOrderId", "a1-c2-entry")], Strip(request)));
        Assert.Equal(3, exchange.Binance.Requests.Count(request => request.Path is "/api/v3/order" or "/fapi/v1/order"));
    }

    [Fact]
    public async Task Accounts_are_read_with_their_balances()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        exchange.Binance.On(HttpMethod.Get, "/api/v3/account", _ => FakeBinance.Json(BinanceSamples.SpotAccount));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v3/account", _ => FakeBinance.Json(BinanceSamples.FuturesAccount));

        var spot = await exchange.Account.GetAccountAsync(Market.Spot, key, Cancellation);
        var futures = await exchange.Account.GetAccountAsync(Market.Futures, key, Cancellation);

        Assert.Equal(true, spot.CanTrade);
        Assert.Equal([new AssetBalance("BTC", 0.0015m, 0.001m), new AssetBalance("USDT", 250.12m, 250.12m)], spot.Balances);
        Assert.Equal((null, 103.5m, 103.12345678m), (futures.CanTrade, futures.TotalMarginBalance, futures.AvailableBalance));
        Assert.Equal([new AssetBalance("USDT", 103.12345678m, 103.12345678m)], futures.Balances);
    }

    [Fact]
    public async Task Reduce_only_is_refused_on_Spot_before_anything_is_sent()
    {
        await using var exchange = new ExchangeHarness(postgres);

        await Assert.ThrowsAsync<ArgumentException>(() => exchange.Trading.PlaceOrderAsync(
            Market.Spot,
            ExchangeHarness.HmacKey(),
            new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.Market, 0.1m, "a1-c2-close", RequestPriority.Normal, reduceOnly: true),
            Cancellation));

        Assert.Empty(exchange.Binance.Requests);
    }

    public static TheoryData<string, Func<OrderRequest>> InvalidOrders => new()
    {
        { "lower-case symbol", () => new OrderRequest("btcusdt", OrderSide.Buy, OrderType.Market, 1m, "id", RequestPriority.Normal) },
        { "symbol with a separator", () => new OrderRequest("BTC&USDT", OrderSide.Buy, OrderType.Market, 1m, "id", RequestPriority.Normal) },
        { "empty client order ID", () => new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, "", RequestPriority.Normal) },
        { "37-character client order ID", () => new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, new string('a', 37), RequestPriority.Normal) },
        { "client order ID with a space", () => new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, "a b", RequestPriority.Normal) },
        { "zero quantity", () => new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 0m, "id", RequestPriority.Normal) },
        { "limit without a price", () => new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Limit, 1m, "id", RequestPriority.Normal) },
        { "market with a price", () => new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, "id", RequestPriority.Normal, price: 1m) },
        { "stop without a stop price", () => new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, 1m, "id", RequestPriority.Protective) },
        { "stop with a limit price", () => new OrderRequest("BTCUSDT", OrderSide.Sell, OrderType.StopMarket, 1m, "id", RequestPriority.Protective, price: 1m, stopPrice: 1m) },
    };

    [Theory]
    [MemberData(nameof(InvalidOrders))]
    public void An_order_that_Binance_would_refuse_cannot_be_built(string _, Func<OrderRequest> build) =>
        Assert.ThrowsAny<ArgumentException>(build);

    private static List<(string Name, string Value)> Strip(RecordedRequest request) =>
        [.. request.Parameters.Where(parameter => parameter.Name is not ("timestamp" or "recvWindow" or "signature"))];
}
