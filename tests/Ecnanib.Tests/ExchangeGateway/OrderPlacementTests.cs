using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.Shared;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Every order carries a unique client order identifier. If the outcome of a request is unknown,
/// the system queries the order's status before any retry, so an order is never sent twice
/// (FR-BIN-6). This is the gateway's part of it: an order is one call that carries the
/// identifier, how the call ended says whether there is an order, and nothing here makes a call
/// again. See "The orders" in docs/exchange.md.
/// </summary>
public class OrderPlacementTests
{
    private const string Id = "0193f3a1b2c34d5e8f9a0b1c2d3e4f50";

    private static readonly ExchangeOrderRequest Buy = new(
        Id, MarketService.Symbol, ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.01m, Price: null, Closes: false);

    private const string TakeProfitId = "0193f3a1b2c34d5e8f9a0b1c2d3e4f51";
    private const string PairId = "0193f3a1b2c34d5e8f9a0b1c2d3e4f52";

    private static readonly ExchangeOrderRequest StopLoss = new(
        Id, MarketService.Symbol, ExchangeOrderSide.Sell, ExchangeOrderType.Stop, 0.01m, Price: 59000.5m, Closes: true);

    private static readonly ExchangeOrderRequest BuyAtAPrice = new(
        Id, MarketService.Symbol, ExchangeOrderSide.Buy, ExchangeOrderType.Limit, 0.01m, Price: 59900.5m, Closes: false);

    private static readonly ExchangePairRequest Pair = new(
        PairId, MarketService.Symbol, 0.01m, StopLossId: Id, StopLossPrice: 59000.5m, TakeProfitId, TakeProfitPrice: 62000.5m);

    [Theory]
    [MemberData(nameof(BinanceCalls.MarketsAndEnvironments), MemberType = typeof(BinanceCalls))]
    public async Task An_order_at_the_market_is_one_signed_call_that_carries_its_identifier_and_has_no_body(Market market, TradingMode mode)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

        var result = await orders.PlaceAsync(BinanceCalls.User, market, mode, apiKey, secretKey, Buy, TestContext.Current.CancellationToken);

        // One call, to the address of the market and the environment, with everything in its
        // query and nothing in a body. It asks for an answer that says what became of the order.
        var call = Assert.Single(binance.System.Calls);
        Assert.Equal(
            $"POST {BinanceCalls.AddressOf(market, mode)}/{(market == Market.Spot ? "api/v3/order" : "fapi/v1/order")}",
            $"{call.Method} {call.Endpoint}");
        Assert.Null(call.Body);
        Assert.Equal(
            [
                ("symbol", MarketService.Symbol), ("side", "BUY"), ("type", "MARKET"), ("quantity", "0.01"),
                ("newClientOrderId", Id), ("newOrderRespType", market == Market.Spot ? "FULL" : "RESULT"), ("recvWindow", "5000"),
            ],
            call.Query.Take(7));
        Assert.Equal(["timestamp", "signature"], call.Query.Skip(7).Select(parameter => parameter.Name));

        // Signed with the secret key of the credential, which is not sent, beside the API key.
        Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);
        Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);
        Assert.DoesNotContain(secretKey, call.Address.AbsoluteUri);

        // The answer is the order as Binance has it: executed, at once.
        Assert.Equal(ExchangeOrderOutcome.Answered, result.Outcome);
        Assert.Equal((ExchangeOrderStatus.Filled, 0.01m, 60000m), (result.Order!.Status, result.Order.ExecutedQuantity, result.Order.AveragePrice));
        Assert.True(result.Order.IsOver);

        // The answer of the Spot market has the executions, with the fee of each.
        Assert.Equal(market == Market.Spot ? 1 : 0, result.Order.Fills.Count);

        // Neither key is in the log, and nothing of the order but that it was sent.
        var log = application.LogText();
        Assert.DoesNotContain(apiKey, log);
        Assert.DoesNotContain(secretKey, log);
        Assert.DoesNotContain(Id, log);
    }

    [Fact]
    public async Task An_order_that_only_closes_is_sent_to_the_Futures_market_as_one_that_only_reduces()
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);
        var close = Buy with { Side = ExchangeOrderSide.Sell, Closes = true };

        // So that it opens nothing when the position is gone already. The Spot market has no
        // positions: a sale there sells what the account holds.
        await orders.PlaceAsync(BinanceCalls.User, Market.Futures, TradingMode.Demo, apiKey, secretKey, close, TestContext.Current.CancellationToken);
        await orders.PlaceAsync(BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, close, TestContext.Current.CancellationToken);

        Assert.Contains(("reduceOnly", "true"), binance.System.Calls[0].Query);
        Assert.DoesNotContain(binance.System.Calls[1].Query, parameter => parameter.Name == "reduceOnly");
    }

    [Fact]
    public async Task A_stop_is_an_order_of_the_Spot_market_and_a_conditional_order_of_the_Futures_market()
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var onSpot = await orders.PlaceAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, StopLoss, TestContext.Current.CancellationToken);
        var onFutures = await orders.PlaceAsync(
            BinanceCalls.User, Market.Futures, TradingMode.Demo, apiKey, secretKey, StopLoss with { ClientOrderId = "other-id_1.2:3/4" },
            TestContext.Current.CancellationToken);

        // On Spot: the stop-loss that is placed at the market when the price reaches it.
        var spot = binance.System.Calls[0];
        Assert.Equal("POST /api/v3/order", $"{spot.Method} {spot.Address.AbsolutePath}");
        Assert.Equal(
            [
                ("symbol", MarketService.Symbol), ("side", "SELL"), ("type", "STOP_LOSS"), ("quantity", "0.01"), ("stopPrice", "59000.5"),
                ("newClientOrderId", Id), ("newOrderRespType", "RESULT"),
            ],
            spot.Query.Take(7));

        // On Futures Binance takes a stop with the calls of its conditional orders only, under
        // a name of its own for the identifier.
        var futures = binance.System.Calls[1];
        Assert.Equal("POST /fapi/v1/algoOrder", $"{futures.Method} {futures.Address.AbsolutePath}");
        Assert.Equal(
            [
                ("algoType", "CONDITIONAL"), ("symbol", MarketService.Symbol), ("side", "SELL"), ("type", "STOP_MARKET"), ("quantity", "0.01"),
                ("triggerPrice", "59000.5"), ("clientAlgoId", "other-id_1.2:3/4"), ("reduceOnly", "true"),
            ],
            futures.Query.Take(8));
        Assert.All(binance.System.Calls, call => Assert.Null(call.Body));

        // Both rest, and nothing of either is executed.
        Assert.All([onSpot, onFutures], result =>
        {
            Assert.Equal(ExchangeOrderOutcome.Answered, result.Outcome);
            Assert.Equal((ExchangeOrderStatus.Open, 0m, null, false), (result.Order!.Status, result.Order.ExecutedQuantity, result.Order.AveragePrice, result.Order.IsOver));
        });
    }

    [Theory]
    [InlineData(Market.Spot, "POST /api/v3/order", "FULL")]
    [InlineData(Market.Futures, "POST /fapi/v1/order", "RESULT")]
    public async Task An_order_at_a_price_rests_until_it_is_executed_or_taken_off(Market market, string asks, string answers)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);
        var which = new ExchangeOrderRef(Id, MarketService.Symbol, ExchangeOrderType.Limit);

        // Below the price of the symbol, which is 60000: nothing sells there yet.
        var placed = await orders.PlaceAsync(
            BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, BuyAtAPrice, TestContext.Current.CancellationToken);

        // The order of each market, good until it is taken off, at its price (FR-BIN-S2).
        var call = Assert.Single(binance.System.Calls);
        Assert.Equal(asks, $"{call.Method} {call.Address.AbsolutePath}");
        Assert.Null(call.Body);
        Assert.Equal(
            [
                ("symbol", MarketService.Symbol), ("side", "BUY"), ("type", "LIMIT"), ("timeInForce", "GTC"), ("quantity", "0.01"),
                ("price", "59900.5"), ("newClientOrderId", Id), ("newOrderRespType", answers),
            ],
            call.Query.Take(8));

        Assert.Equal(ExchangeOrderOutcome.Answered, placed.Outcome);
        Assert.Equal((ExchangeOrderStatus.Open, 0m, null, false), (placed.Order!.Status, placed.Order.ExecutedQuantity, placed.Order.AveragePrice, placed.Order.IsOver));

        // A part of it is executed, at its price, and the rest still rests.
        binance.Execute(Assert.Single(binance.Orders), 0.004m);

        var partly = await orders.FindAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal(
            (ExchangeOrderStatus.PartiallyFilled, 0.004m, 59900.5m, false),
            (partly.Order!.Status, partly.Order.ExecutedQuantity, partly.Order.AveragePrice, partly.Order.IsOver));

        // Taken off, it is over, and says what was executed of it before.
        var taken = await orders.CancelAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal(
            (ExchangeOrderOutcome.Answered, ExchangeOrderStatus.Canceled, 0.004m, true),
            (taken.Outcome, taken.Order!.Status, taken.Order.ExecutedQuantity, taken.Order.IsOver));
    }

    [Fact]
    public async Task An_order_at_a_price_that_the_market_has_reached_is_executed_at_once_and_says_its_executions_on_the_Spot_market()
    {
        var binance = new TradingBinance { FeeAsset = "BTC" };
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var placed = await orders.PlaceAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, BuyAtAPrice with { Price = 60100m },
            TestContext.Current.CancellationToken);

        // At the price of the symbol, which is better than the one of the order, with the fee
        // of the execution in the asset that was bought.
        Assert.Equal((ExchangeOrderStatus.Filled, 0.01m, 60000m), (placed.Order!.Status, placed.Order.ExecutedQuantity, placed.Order.AveragePrice));

        var fill = Assert.Single(placed.Order.Fills);
        Assert.Equal((0.01m, 0.00001m, "BTC"), (fill.Quantity, fill.Fee, fill.FeeAsset));
    }

    [Theory]
    [InlineData(TradingMode.Live)]
    [InlineData(TradingMode.Demo)]
    public async Task The_pair_of_the_Spot_market_is_one_signed_call_that_carries_an_identifier_for_the_pair_and_for_each_of_its_orders(TradingMode mode)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

        var result = await orders.PlacePairAsync(BinanceCalls.User, mode, apiKey, secretKey, Pair, TestContext.Current.CancellationToken);

        // One call, to the address of the Spot market in the environment, with everything in
        // its query. Both orders sell the same quantity: the take-profit above the price and
        // the stop-loss below it, each placed at the market when the price reaches its trigger,
        // and Binance ends the other then (FR-BIN-S2).
        var call = Assert.Single(binance.System.Calls);
        Assert.Equal($"POST {BinanceCalls.AddressOf(Market.Spot, mode)}/api/v3/orderList/oco", $"{call.Method} {call.Endpoint}");
        Assert.Null(call.Body);
        Assert.Equal(
            [
                ("symbol", MarketService.Symbol), ("side", "SELL"), ("quantity", "0.01"), ("listClientOrderId", PairId),
                ("aboveType", "TAKE_PROFIT"), ("aboveStopPrice", "62000.5"), ("aboveClientOrderId", TakeProfitId),
                ("belowType", "STOP_LOSS"), ("belowStopPrice", "59000.5"), ("belowClientOrderId", Id),
                ("newOrderRespType", "RESULT"), ("recvWindow", "5000"),
            ],
            call.Query.Take(12));
        Assert.Equal(["timestamp", "signature"], call.Query.Skip(12).Select(parameter => parameter.Name));
        Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);
        Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);

        // Both rest, and each is said under its own number.
        Assert.Equal(ExchangeOrderOutcome.Answered, result.Outcome);
        Assert.Equal((ExchangeOrderStatus.Open, 0m), (result.StopLoss!.Status, result.StopLoss.ExecutedQuantity));
        Assert.Equal((ExchangeOrderStatus.Open, 0m), (result.TakeProfit!.Status, result.TakeProfit.ExecutedQuantity));
        Assert.Equal($"{binance.The(stop: true).Id}", result.StopLoss.ExchangeOrderId);
        Assert.Equal($"{binance.TheTakeProfit().Id}", result.TakeProfit.ExchangeOrderId);

        // No identifier of the pair is in the log.
        var log = application.LogText();
        Assert.All([PairId, Id, TakeProfitId, apiKey, secretKey], secret => Assert.DoesNotContain(secret, log));
    }

    [Fact]
    public async Task Each_order_of_a_pair_is_asked_about_by_its_own_identifier_and_when_one_is_executed_the_other_has_ended()
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);
        var stopLoss = new ExchangeOrderRef(Id, MarketService.Symbol, ExchangeOrderType.Stop);
        var takeProfit = new ExchangeOrderRef(TakeProfitId, MarketService.Symbol, ExchangeOrderType.Stop);

        await orders.PlacePairAsync(BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, Pair, TestContext.Current.CancellationToken);

        // The price reaches the take-profit.
        binance.Execute(binance.TheTakeProfit());

        var executed = await orders.FindAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, takeProfit, TestContext.Current.CancellationToken);
        var ended = await orders.FindAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, stopLoss, TestContext.Current.CancellationToken);

        Assert.Equal(["GET /api/v3/order", "GET /api/v3/order"], binance.OrderCalls.Skip(1));
        Assert.Equal([TakeProfitId, Id], binance.AskedAbout);
        Assert.Equal((ExchangeOrderStatus.Filled, 0.01m, 62000.5m), (executed.Order!.Status, executed.Order.ExecutedQuantity, executed.Order.AveragePrice));
        Assert.Equal((ExchangeOrderStatus.Expired, 0m, true), (ended.Order!.Status, ended.Order.ExecutedQuantity, ended.Order.IsOver));
    }

    [Fact]
    public async Task A_pair_that_rests_is_taken_off_the_exchange_with_one_call_by_the_identifier_of_the_pair()
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);
        var which = new ExchangePairRef(PairId, MarketService.Symbol, Id, TakeProfitId);

        await orders.PlacePairAsync(BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, Pair, TestContext.Current.CancellationToken);

        var taken = await orders.CancelPairAsync(BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        var call = binance.System.Calls[^1];
        Assert.Equal("DELETE /api/v3/orderList", $"{call.Method} {call.Address.AbsolutePath}");
        Assert.Equal([("symbol", MarketService.Symbol), ("listClientOrderId", PairId)], call.Query.Take(2));
        Assert.Null(call.Body);

        // Binance reports each order that it took off under the identifier that the order
        // carried, and the answer is read by that.
        Assert.Equal(ExchangeOrderOutcome.Answered, taken.Outcome);
        Assert.Equal((ExchangeOrderStatus.Canceled, $"{binance.The(stop: true).Id}"), (taken.StopLoss!.Status, taken.StopLoss.ExchangeOrderId));
        Assert.Equal((ExchangeOrderStatus.Canceled, $"{binance.TheTakeProfit().Id}"), (taken.TakeProfit!.Status, taken.TakeProfit.ExchangeOrderId));
        Assert.All(binance.Orders, order => Assert.Equal("CANCELED", order.Status));

        // Taken off once more, no such pair rests.
        var again = await orders.CancelPairAsync(BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal((ExchangeOrderOutcome.NotFound, null, null), (again.Outcome, again.StopLoss, again.TakeProfit));
    }

    [Theory]

    // Binance refused the pair: there is neither order.
    [InlineData(400, """{ "code": -2010, "msg": "The relationship of the prices for the orders is not correct." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(400, """{ "code": -2010, "msg": "Account has insufficient balance for requested action." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(401, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""", ExchangeOrderOutcome.KeyRefused)]
    [InlineData(429, """{ "code": -1003, "msg": "Too many requests." }""", ExchangeOrderOutcome.NotSent)]

    // What became of the call is not known: there may be both.
    [InlineData(503, "", ExchangeOrderOutcome.Unknown)]
    [InlineData(400, """{ "code": -1007, "msg": "Timeout waiting for response from backend server. Send status unknown; execution status unknown." }""", ExchangeOrderOutcome.Unknown)]

    // An answer that says that the call went well, and does not say both orders: the same.
    [InlineData(200, "{}", ExchangeOrderOutcome.Unknown)]
    [InlineData(200, """{ "orderListId": 7, "orderReports": [ { "orderId": 28, "clientOrderId": "0193f3a1b2c34d5e8f9a0b1c2d3e4f50", "status": "NEW", "executedQty": "0.00000000" } ] }""", ExchangeOrderOutcome.Unknown)]
    public async Task How_the_call_that_places_a_pair_ends_says_whether_there_are_its_orders_and_the_call_is_not_made_again(
        int status, string body, ExchangeOrderOutcome expected)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json((HttpStatusCode)status, body));
        using var application = BinanceCalls.Behind(binance);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var result = await orders.PlacePairAsync(BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, Pair, TestContext.Current.CancellationToken);

        Assert.Equal((expected, null, null), (result.Outcome, result.StopLoss, result.TakeProfit));
        Assert.Single(binance.Calls);
    }

    [Theory]
    [InlineData(Id, Id, TakeProfitId)]
    [InlineData(PairId, Id, Id)]
    [InlineData(PairId, "has a space", TakeProfitId)]
    public async Task A_pair_whose_identifiers_are_not_three_that_Binance_takes_is_an_error_of_the_caller_and_nothing_is_sent(
        string pairId, string stopLossId, string takeProfitId)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        // An order is asked about by its own identifier, and Binance keeps the one of the pair
        // apart from those of its orders.
        await Assert.ThrowsAsync<ArgumentException>(() => orders.PlacePairAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey,
            Pair with { PairId = pairId, StopLossId = stopLossId, TakeProfitId = takeProfitId }, TestContext.Current.CancellationToken));

        Assert.Empty(binance.System.Calls);
    }

    [Theory]

    // Binance refused the order with one of its errors: there is no order.
    [InlineData(400, """{ "code": -2010, "msg": "Order would trigger immediately." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(400, """{ "code": -2010, "msg": "Account has insufficient balance for requested action." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(400, """{ "code": -1013, "msg": "Filter failure: LOT_SIZE" }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(400, """{ "code": -1021, "msg": "Timestamp for this request is outside of the recvWindow." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(400, """{ "code": -4120, "msg": "Order type not supported for this endpoint." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(403, "<html>Request blocked</html>", ExchangeOrderOutcome.Rejected)]

    // It answered as it answers a wrong credential: there is no order, and the key is checked again.
    [InlineData(401, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""", ExchangeOrderOutcome.KeyRefused)]
    [InlineData(400, """{ "code": -1022, "msg": "Signature for this request is not valid." }""", ExchangeOrderOutcome.KeyRefused)]

    // It answered that it has had too many requests: the call did not reach its orders.
    [InlineData(429, """{ "code": -1003, "msg": "Too many requests." }""", ExchangeOrderOutcome.NotSent)]
    [InlineData(418, """{ "code": -1003, "msg": "Way too many requests; IP banned." }""", ExchangeOrderOutcome.NotSent)]

    // A fault of Binance, its own timeout, and an error of which it says that the execution
    // is unknown: there may be an order.
    [InlineData(500, "", ExchangeOrderOutcome.Unknown)]
    [InlineData(503, """{ "code": -1001, "msg": "Internal error; unable to process your request. Please try again." }""", ExchangeOrderOutcome.Unknown)]
    [InlineData(503, """{ "msg": "Unknown error, please check your request or try again later." }""", ExchangeOrderOutcome.Unknown)]
    [InlineData(504, "<html>Gateway Time-out</html>", ExchangeOrderOutcome.Unknown)]
    [InlineData(408, "", ExchangeOrderOutcome.Unknown)]
    [InlineData(400, """{ "code": -1007, "msg": "Timeout waiting for response from backend server. Send status unknown; execution status unknown." }""", ExchangeOrderOutcome.Unknown)]
    [InlineData(400, """{ "code": -1006, "msg": "An unexpected response was received from the message bus. Execution status unknown." }""", ExchangeOrderOutcome.Unknown)]
    [InlineData(400, """{ "code": -1000, "msg": "An unknown error occurred while processing the request." }""", ExchangeOrderOutcome.Unknown)]

    // An answer that says that the call went well, and does not read: there may be an order.
    [InlineData(200, "<html>ok</html>", ExchangeOrderOutcome.Unknown)]
    [InlineData(200, "{}", ExchangeOrderOutcome.Unknown)]
    [InlineData(200, """{ "orderId": 28, "status": "SOMETHING_NEW", "executedQty": "0.00000000" }""", ExchangeOrderOutcome.Unknown)]
    public async Task How_the_call_that_places_an_order_ends_says_whether_there_is_an_order_and_the_call_is_not_made_again(
        int status, string body, ExchangeOrderOutcome expected)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json((HttpStatusCode)status, body));
        using var application = BinanceCalls.Behind(binance);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var result = await orders.PlaceAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, Buy, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Order);

        // Once, whatever came of it: whether it is made again is for what places the order
        // to decide, after it has asked.
        Assert.Single(binance.Calls);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("no connection")]
    [InlineData("broken answer")]
    public async Task A_call_that_places_an_order_and_gets_no_answer_leaves_the_outcome_unknown(string what)
    {
        var binance = new StandInSystem(_ => what switch
        {
            "timeout" => throw new TaskCanceledException("Binance does not answer."),
            "no connection" => throw new HttpRequestException("The connection was reset."),
            _ => throw new IOException("The answer broke off."),
        });
        using var application = BinanceCalls.Behind(binance);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var result = await orders.PlaceAsync(
            BinanceCalls.User, Market.Futures, TradingMode.Demo, apiKey, secretKey, StopLoss, TestContext.Current.CancellationToken);

        // Binance may have taken the order before the answer was lost.
        Assert.Equal(ExchangeOrderOutcome.Unknown, result.Outcome);
        Assert.Single(binance.Calls);
    }

    [Fact]
    public async Task A_call_that_the_limiter_holds_back_is_not_sent()
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.TooManyRequests, """{ "code": -1003, "msg": "Too many requests." }"""));
        using var application = BinanceCalls.Behind(binance);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        // Binance signals that it has had too many requests, and the limiter sends nothing
        // there for a while (FR-BIN-4).
        await orders.PlaceAsync(BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, Buy, TestContext.Current.CancellationToken);

        var held = await orders.PlaceAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, Buy with { ClientOrderId = "second" },
            TestContext.Current.CancellationToken);

        // The second order never left: there is no order, and nothing to ask about.
        Assert.Equal(ExchangeOrderOutcome.NotSent, held.Outcome);
        Assert.Single(binance.Calls);
    }

    [Fact]
    public async Task Once_an_order_is_sent_its_caller_cannot_cancel_the_call()
    {
        using var stopping = new CancellationTokenSource();
        var binance = new TradingBinance();

        // The caller cancels while Binance has the call.
        binance.Fate = _ =>
        {
            stopping.Cancel();

            return OrderFate.Taken;
        };

        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var result = await orders.PlaceAsync(BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, Buy, stopping.Token);

        // The call ran to its answer: a call that was dropped half way would leave nobody
        // knowing whether Binance took the order.
        Assert.Equal(ExchangeOrderOutcome.Answered, result.Outcome);
        Assert.Equal(ExchangeOrderStatus.Filled, result.Order!.Status);

        // A caller that has cancelled before anything is sent gets an error, and nothing is sent.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orders.PlaceAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, Buy with { ClientOrderId = "second" }, stopping.Token));

        Assert.Single(binance.System.Calls);
    }

    [Theory]
    [InlineData(Market.Spot, false, "GET /api/v3/order", "origClientOrderId")]
    [InlineData(Market.Spot, true, "GET /api/v3/order", "origClientOrderId")]
    [InlineData(Market.Futures, false, "GET /fapi/v1/order", "origClientOrderId")]
    [InlineData(Market.Futures, true, "GET /fapi/v1/algoOrder", "clientAlgoId")]
    public async Task An_order_is_asked_about_by_its_identifier_and_an_order_that_Binance_does_not_have_is_not_found(
        Market market, bool stop, string asks, string named)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);
        var order = stop ? StopLoss : Buy;
        var which = new ExchangeOrderRef(Id, MarketService.Symbol, order.Type);

        // Before it is placed, Binance has no such order: that is its own word, and not a failure.
        var before = await orders.FindAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.NotFound, before.Outcome);

        await orders.PlaceAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, order, TestContext.Current.CancellationToken);
        var after = await orders.FindAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        // Asked with a call that reads, by the identifier that the order carries.
        var call = binance.System.Calls[^1];
        Assert.Equal(asks, $"{call.Method} {call.Address.AbsolutePath}");
        Assert.Contains((named, Id), call.Query);
        Assert.Null(call.Body);

        Assert.Equal(ExchangeOrderOutcome.Answered, after.Outcome);
        Assert.Equal(stop ? ExchangeOrderStatus.Open : ExchangeOrderStatus.Filled, after.Order!.Status);
    }

    [Theory]
    [InlineData(400, """{ "code": -1121, "msg": "Invalid symbol." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(401, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""", ExchangeOrderOutcome.KeyRefused)]
    [InlineData(429, """{ "code": -1003, "msg": "Too many requests." }""", ExchangeOrderOutcome.NotSent)]
    [InlineData(503, "", ExchangeOrderOutcome.Unknown)]
    [InlineData(200, "[]", ExchangeOrderOutcome.Unknown)]
    public async Task Only_the_word_of_Binance_that_it_has_no_such_order_says_that_there_is_none(int status, string body, ExchangeOrderOutcome expected)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json((HttpStatusCode)status, body));
        using var application = BinanceCalls.Behind(binance);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var result = await orders.FindAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, new ExchangeOrderRef(Id, MarketService.Symbol, ExchangeOrderType.Market),
            TestContext.Current.CancellationToken);

        // Every other end of the question says nothing about the order: an order is never
        // taken for one that was not placed because the question failed.
        Assert.Equal(expected, result.Outcome);
        Assert.NotEqual(ExchangeOrderOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(Market.Spot, "DELETE /api/v3/order", "origClientOrderId")]
    [InlineData(Market.Futures, "DELETE /fapi/v1/algoOrder", "clientAlgoId")]
    public async Task A_stop_that_rests_is_taken_off_the_exchange_by_its_identifier(Market market, string asks, string named)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);
        var which = new ExchangeOrderRef(Id, MarketService.Symbol, ExchangeOrderType.Stop);

        await orders.PlaceAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, StopLoss, TestContext.Current.CancellationToken);

        var taken = await orders.CancelAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        var call = binance.System.Calls[^1];
        Assert.Equal(asks, $"{call.Method} {call.Address.AbsolutePath}");
        Assert.Contains((named, Id), call.Query);
        Assert.Null(call.Body);
        Assert.Equal((ExchangeOrderOutcome.Answered, ExchangeOrderStatus.Canceled), (taken.Outcome, taken.Order!.Status));
        Assert.Equal("CANCELED", Assert.Single(binance.Orders).Status);

        // Taken off once more, no such order rests: it was executed, was taken off, or never was.
        var again = await orders.CancelAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.NotFound, again.Outcome);
    }

    [Theory]
    [InlineData(Market.Spot, "GET /api/v3/myTrades")]
    [InlineData(Market.Futures, "GET /fapi/v1/userTrades")]
    public async Task A_stop_that_was_executed_says_so_and_its_executions_are_read_with_their_fees(Market market, string asks)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var placed = await orders.PlaceAsync(BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, StopLoss, TestContext.Current.CancellationToken);
        binance.Execute(Assert.Single(binance.Orders));

        var executed = await orders.FindAsync(
            BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, new ExchangeOrderRef(Id, MarketService.Symbol, ExchangeOrderType.Stop),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (ExchangeOrderStatus.Filled, 0.01m, 59000.5m, true),
            (executed.Order!.Status, executed.Order.ExecutedQuantity, executed.Order.AveragePrice, executed.Order.IsOver));

        // A conditional order of the Futures market that was triggered is known by the order
        // that it became, which is where its executions are. On Spot the order keeps its number.
        Assert.Equal(market == Market.Spot, executed.Order.ExchangeOrderId == placed.Order!.ExchangeOrderId);

        var fills = await orders.FillsAsync(
            BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, executed.Order.ExchangeOrderId,
            TestContext.Current.CancellationToken);

        var call = binance.System.Calls[^1];
        Assert.Equal(asks, $"{call.Method} {call.Address.AbsolutePath}");
        Assert.Contains(("orderId", executed.Order.ExchangeOrderId), call.Query);
        Assert.Equal(ExchangeCallOutcome.Succeeded, fills.Outcome);

        var fill = Assert.Single(fills.Fills);
        Assert.Equal((59000.5m, 0.01m, 0.590005m, "USDT"), (fill.Price, fill.Quantity, fill.Fee, fill.FeeAsset));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794735000250), fill.At);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has a space")]
    [InlineData("0123456789012345678901234567890123456")]
    public async Task An_identifier_that_Binance_would_not_take_is_an_error_of_the_caller_and_nothing_is_sent(string identifier)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        await Assert.ThrowsAsync<ArgumentException>(() => orders.PlaceAsync(
            BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, Buy with { ClientOrderId = identifier },
            TestContext.Current.CancellationToken));

        Assert.Empty(binance.System.Calls);
    }

    [Fact]
    public void A_result_holds_an_order_when_Binance_answered_with_it_and_only_then()
    {
        var order = new ExchangeOrder("28", ExchangeOrderStatus.Open, 0m, null, []);

        Assert.Same(order, new ExchangeOrderResult(ExchangeOrderOutcome.Answered, order).Order);
        Assert.Throws<ArgumentException>(() => new ExchangeOrderResult(ExchangeOrderOutcome.Answered, null));
        Assert.Throws<ArgumentException>(() => new ExchangeOrderResult(ExchangeOrderOutcome.Unknown, order));
        Assert.Null(ExchangeOrderResult.Without(ExchangeOrderOutcome.NotFound).Order);

        // The same of a pair: both of its orders, or neither.
        Assert.Same(order, new ExchangePairResult(ExchangeOrderOutcome.Answered, order, order).TakeProfit);
        Assert.Throws<ArgumentException>(() => new ExchangePairResult(ExchangeOrderOutcome.Answered, order, null));
        Assert.Throws<ArgumentException>(() => new ExchangePairResult(ExchangeOrderOutcome.Answered, null, order));
        Assert.Throws<ArgumentException>(() => new ExchangePairResult(ExchangeOrderOutcome.Rejected, order, order));
        Assert.Null(ExchangePairResult.Without(ExchangeOrderOutcome.Unknown).StopLoss);
    }
}
