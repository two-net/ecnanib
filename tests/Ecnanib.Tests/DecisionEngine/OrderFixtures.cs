using System.Globalization;
using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>Rows of orders that a test needs an agent to have, without a cycle that places them.</summary>
internal static class StoredOrderRows
{
    /// <summary>
    /// Gives an agent of a user the orders of a position that is over, stored as the application
    /// stores them: an entry that was executed, with its one execution, the position that it
    /// opened, what the position paid as funding, and the stop-loss that closed it. Everything is over, so the agent holds nothing,
    /// and nothing is left to settle. It is for a test that is about something else, and needs a
    /// row in every table that holds rows of users.
    /// </summary>
    /// <returns>The id of the position.</returns>
    public static async Task<Guid> GiveOrdersAsync(
        this AccountsApplication application, Guid userId, Guid agentId, Guid? cycleId, DateTimeOffset at)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var agent = await context.Set<Agent>().AsNoTracking().SingleAsync(stored => stored.Id == agentId, TestContext.Current.CancellationToken);

        var position = new Position(
            Guid.CreateVersion7(),
            agentId,
            userId,
            agent.Mode,
            agent.Market,
            agent.Symbol,
            PositionSide.Long,
            entryPrice: 100_000m,
            quantity: 0.01m,
            stopLoss: 99_000m,
            takeProfit: 102_000m,
            openedAt: at,
            closedAt: at.AddMinutes(30),
            ExitReason.StopLoss,
            alertedAt: null,
            fundingDueAt: null);

        var entryId = Guid.CreateVersion7();
        var entry = new Order(
            entryId,
            agentId,
            userId,
            agent.Mode,
            agent.Market,
            agent.Symbol,
            cycleId,
            position.Id,
            Order.ClientOrderIdOf(entryId),
            exchangeOrderId: "7001",
            OrderPurpose.Entry,
            OrderType.Market,
            OrderSide.Buy,
            price: null,
            stopLoss: 99_000m,
            takeProfit: 102_000m,
            quantity: 0.01m,
            OrderStatus.Filled,
            executedQuantity: 0.01m,
            averagePrice: 100_000m,
            exitReason: null,
            sends: 1,
            fillsRecorded: true,
            createdAt: at,
            updatedAt: at,
            checkedAt: at,
            pairId: null,
            cancelAfter: null);

        var stopLossId = Guid.CreateVersion7();
        var stopLoss = new Order(
            stopLossId,
            agentId,
            userId,
            agent.Mode,
            agent.Market,
            agent.Symbol,
            cycleId,
            position.Id,
            Order.ClientOrderIdOf(stopLossId),
            exchangeOrderId: "7002",
            OrderPurpose.StopLoss,
            OrderType.Stop,
            OrderSide.Sell,
            price: 99_000m,
            stopLoss: null,
            takeProfit: null,
            quantity: 0.01m,
            OrderStatus.Filled,
            executedQuantity: 0.01m,
            averagePrice: 98_990m,
            exitReason: null,
            sends: 1,
            fillsRecorded: true,
            createdAt: at.AddSeconds(1),
            updatedAt: at.AddMinutes(30),
            checkedAt: at.AddMinutes(30),
            pairId: null,
            cancelAfter: null);

        // The position first: its orders name it.
        context.Add(position);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Add(entry);
        context.Add(stopLoss);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Add(new Fill(Guid.CreateVersion7(), entryId, userId, tradeId: "9001", 100_000m, 0.01m, fee: 0.5m, feeAsset: "USDT", at));

        // What the position paid as funding while it was open, as the exchange says it of a
        // position of the Futures market. It is a row of the user in one more table.
        context.Add(new Funding(Guid.CreateVersion7(), position.Id, userId, transactionId: "8001", amount: -0.1m, asset: "USDT", at.AddMinutes(10)));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return position.Id;
    }
}

/// <summary>What a Binance of the tests does with a call that places an order.</summary>
internal enum OrderFate
{
    /// <summary>It takes the order, and answers with it.</summary>
    Taken,

    /// <summary>It takes the order, and the answer never comes: the call ends as one that Binance did not answer in time.</summary>
    TakenUnsaid,

    /// <summary>It does not take the order, and no answer comes either: the call ends the same way.</summary>
    Lost,

    /// <summary>It refuses the order, with one of its errors.</summary>
    Refused,

    /// <summary>It answers with a fault of its own, and takes no order.</summary>
    Faulted,
}

/// <summary>An order as a <see cref="TradingBinance"/> was asked to place it.</summary>
/// <param name="Market">The market whose address the call went to.</param>
/// <param name="ClientOrderId">The identifier that the order carries. Of a pair, the one of its stop-loss.</param>
/// <param name="Stop">Whether it is a stop. A pair is asked for as its stop-loss, with its take-profit beside it.</param>
/// <param name="Side">Binance's name of the side.</param>
/// <param name="Quantity">Its quantity.</param>
/// <param name="Trigger">The price that a stop is triggered at.</param>
/// <param name="ReduceOnly">Whether it was sent as an order that only reduces a position.</param>
/// <param name="Limit">The price of an order at a price.</param>
/// <param name="TakeProfit">Of a pair: the price that its take-profit is triggered at.</param>
/// <param name="TakeProfitId">Of a pair: the identifier that its take-profit carries.</param>
/// <param name="PairId">Of a pair: the identifier that the pair carries.</param>
/// <param name="Type">Binance's name of its type, such as <c>STOP_MARKET</c> or <c>TAKE_PROFIT_MARKET</c>. Null for a pair.</param>
/// <param name="WorkingType">The price that a conditional order of the Futures market is triggered on, as it was asked for.</param>
internal sealed record AskedOrder(
    Market Market,
    string ClientOrderId,
    bool Stop,
    string Side,
    decimal Quantity,
    decimal? Trigger,
    bool ReduceOnly,
    decimal? Limit = null,
    decimal? TakeProfit = null,
    string? TakeProfitId = null,
    string? PairId = null,
    string? Type = null,
    string? WorkingType = null)
{
    /// <summary>Whether it is a take-profit that is an order of its own, as the Futures market has it.</summary>
    public bool TakesProfit => Type is "TAKE_PROFIT_MARKET" or "TAKE_PROFIT" && PairId is null && Stop;
}

/// <summary>An order that a <see cref="TradingBinance"/> has.</summary>
internal sealed class KeptOrder(long id, AskedOrder asked, string type)
{
    public long Id { get; } = id;

    /// <summary>
    /// The order as it was asked for. The stop-loss of a pair says the whole pair, and its
    /// take-profit says itself: a stop at the price of the take-profit, under its own identifier.
    /// </summary>
    public AskedOrder Asked { get; } = asked;

    /// <summary>
    /// Binance's name of its type: <c>MARKET</c>, <c>LIMIT</c>, <c>STOP_LOSS</c>, <c>TAKE_PROFIT</c>, <c>STOP_MARKET</c>, or
    /// <c>TAKE_PROFIT_MARKET</c>.
    /// </summary>
    public string Type { get; } = type;

    /// <summary>Binance's word for its state: <c>NEW</c>, <c>PARTIALLY_FILLED</c>, <c>FILLED</c>, <c>CANCELED</c>, or <c>EXPIRED</c>.</summary>
    public string Status { get; set; } = "NEW";

    public decimal Executed { get; set; }

    public decimal Price { get; set; }

    /// <summary>The id of the order that a conditional order of the Futures market became when it was triggered.</summary>
    public long? Became { get; set; }

    /// <summary>The number that Binance gave the list of a pair, when the order is one of a pair.</summary>
    public long? ListId { get; init; }

    /// <summary>The other order of its pair.</summary>
    public KeptOrder? Sibling { get; set; }

    /// <summary>Whether it is a take-profit: the one of a pair, or the order of its own that the Futures market has.</summary>
    public bool TakeProfit => Type is "TAKE_PROFIT" or "TAKE_PROFIT_MARKET";

    /// <summary>Whether it is with the exchange still.</summary>
    public bool Rests => Status is "NEW" or "PARTIALLY_FILLED";

    /// <summary>The executions: the number of each, its quantity, its fee, and the asset of the fee.</summary>
    public List<(long TradeId, decimal Quantity, decimal Fee, string FeeAsset)> Trades { get; } = [];
}

/// <summary>
/// A Binance of the tests that keeps orders: it takes an order that is placed, answers what
/// became of it, takes it off, and lists its executions, in the form that Binance's
/// documentation gives. An order at the market is executed at once, at <see cref="Price"/>. An
/// order at a price that the market does not reach rests, and so does a stop, until the test has
/// it executed. A pair of the Spot market is two stops of one list: when one is executed the
/// other ends, and taking one off takes both. A test says what becomes of a call that places an
/// order with <see cref="Fate"/>, and of any other call with <see cref="Instead"/>. Every other
/// call of the gateway is answered as <see cref="BinanceCalls.AnswerTo(string)"/> answers it.
/// On the Futures market a stop and a take-profit are conditional orders of their own, which
/// Binance keeps apart from the other orders: neither ends when the other is executed, and one
/// whose trigger the price has passed is refused. The margin of a symbol is isolated once it
/// was asked to be, and its leverage is the one that was last asked for.
/// </summary>
internal sealed class TradingBinance
{
    private const string BaseAsset = "BTC";
    private const string PairPath = "/api/v3/orderList/oco";
    private const string PairCancelPath = "/api/v3/orderList";
    private const string MarginPath = "/fapi/v1/marginType";
    private const string LeveragePath = "/fapi/v1/leverage";
    private const long At = 1794735000250;

    private readonly Lock _gate = new();
    private readonly List<KeptOrder> _orders = [];
    private long _next = 5000;

    public TradingBinance() => System = new StandInSystem(Answer);

    /// <summary>What stands behind the client of the gateway, and keeps every call.</summary>
    public StandInSystem System { get; }

    /// <summary>The price that an order at the market is executed at.</summary>
    public decimal Price { get; set; } = 60000m;

    /// <summary>The asset that Binance takes its fee in. On the Spot market it may be the asset that is bought.</summary>
    public string FeeAsset { get; set; } = "USDT";

    /// <summary>
    /// When Binance says that the executions of its orders were, where it lists them: a moment
    /// of its own clock, which a test sets when it is about that moment.
    /// </summary>
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(At);

    /// <summary>What becomes of a call that places an order, or a pair, when it is not taken and answered.</summary>
    public Func<AskedOrder, OrderFate>? Fate { get; set; }

    /// <summary>What a call is answered with in place of everything else, when this gives an answer. It may fail, as a connection does.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Instead { get; set; }

    /// <summary>The orders that Binance has, in the order it took them. A pair is two: its stop-loss, and then its take-profit.</summary>
    public IReadOnlyList<KeptOrder> Orders
    {
        get
        {
            lock (_gate)
            {
                return [.. _orders];
            }
        }
    }

    /// <summary>The calls that placed an order or a pair, in the order they came: each as what it asked for.</summary>
    public IReadOnlyList<AskedOrder> Asked =>
        [
            .. System.Calls
                .Where(call => call.Method == "POST" && (IsOrderPath(call.Address.AbsolutePath) || call.Address.AbsolutePath == PairPath))
                .Select(call => AskedOf(call.Address)),
        ];

    /// <summary>Whether the symbol has isolated margin in the account: it has once it was asked to have.</summary>
    public bool Isolated { get; private set; }

    /// <summary>The leverages that the symbol was asked to have, in the order they were asked for.</summary>
    public IReadOnlyList<int> Leverages =>
        [
            .. System.Calls
                .Where(call => call.Method == "POST" && call.Address.AbsolutePath == LeveragePath)
                .Select(call => int.Parse(ParameterOf(call.Address, "leverage")!, CultureInfo.InvariantCulture)),
        ];

    /// <summary>The calls that set the margin of the symbol or placed an order or a pair, each as its path, in the order they came.</summary>
    public IReadOnlyList<string> Changes =>
        [.. System.Calls.Where(call => call.Method == "POST").Select(call => call.Address.AbsolutePath)];

    /// <summary>The calls that asked what became of an order, by the identifier each asked with, in the order they came.</summary>
    public IReadOnlyList<string> AskedAbout =>
        [
            .. System.Calls
                .Where(call => call.Method == "GET" && IsOrderPath(call.Address.AbsolutePath))
                .Select(call => IdentifierOf(call.Address)),
        ];

    /// <summary>The calls about orders and pairs, each as its method and its path, in the order they came.</summary>
    public IReadOnlyList<string> OrderCalls =>
        [
            .. System.Calls
                .Where(call => IsOrderPath(call.Address.AbsolutePath)
                    || call.Address.AbsolutePath is PairPath or PairCancelPath
                    || call.Address.AbsolutePath.EndsWith("Trades", StringComparison.Ordinal))
                .Select(call => $"{call.Method} {call.Address.AbsolutePath}"),
        ];

    /// <summary>The one order that Binance has of a kind, which is no take-profit: an entry or a close, or a stop-loss.</summary>
    public KeptOrder The(bool stop) => Assert.Single(Orders, order => order.Asked.Stop == stop && !order.TakeProfit);

    /// <summary>The one take-profit that Binance has.</summary>
    public KeptOrder TheTakeProfit() => Assert.Single(Orders, order => order.TakeProfit);

    /// <summary>
    /// Has an order that rests executed, as when the price reaches it: a stop at its trigger, and
    /// an order at a price at that price, in full or for a part of it. The other order of a pair
    /// ends then.
    /// </summary>
    public void Execute(KeptOrder order, decimal? quantity = null)
    {
        lock (_gate)
        {
            Assert.True(order.Rests, "An order that is executed is one that rests.");

            var executed = quantity ?? (order.Asked.Quantity - order.Executed);
            var price = order.Asked.Limit ?? order.Asked.Trigger ?? Price;

            order.Became ??= order.Asked.Market == Market.Futures && order.Asked.Stop ? ++_next : null;

            // A stop sells or buys back at the market, and its fee is taken in what it is priced
            // in. A purchase at a price may have its fee taken in what was bought.
            Fill(order, executed, price, order.Asked.Stop ? "USDT" : FeeAsset);

            if (order.Sibling is { Rests: true } other)
            {
                other.Status = "EXPIRED";
            }
        }
    }

    /// <summary>Takes an order off the exchange, as its user does at Binance. The other order of a pair goes with it.</summary>
    public void TakeOff(KeptOrder order)
    {
        lock (_gate)
        {
            order.Status = "CANCELED";

            if (order.Sibling is { Rests: true } other)
            {
                other.Status = "CANCELED";
            }
        }
    }

    private Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Instead?.Invoke(request) is { } other)
        {
            return Task.FromResult(other);
        }

        var address = request.RequestUri!;
        var path = address.AbsolutePath;
        var market = path.StartsWith("/fapi/", StringComparison.Ordinal) ? Market.Futures : Market.Spot;

        lock (_gate)
        {
            return Task.FromResult((request.Method.Method, path) switch
            {
                ("POST", PairPath) => PlacePair(AskedOf(address)),
                ("DELETE", PairCancelPath) => CancelPair(ParameterOf(address, "listClientOrderId")!),
                ("POST", MarginPath) => Isolate(),
                ("POST", LeveragePath) => Json(BinanceCalls.AnswerTo(request)),
                ("POST", _) when IsOrderPath(path) => Place(AskedOf(address), ParameterOf(address, "type")!),
                ("GET", _) when IsOrderPath(path) => Find(IdentifierOf(address), path) is { } found ? Json(Body(found, full: false)) : NoSuchOrder(-2013),
                ("DELETE", _) when IsOrderPath(path) => Cancel(IdentifierOf(address), path),
                ("GET", "/api/v3/myTrades" or "/fapi/v1/userTrades") => Json(Trades(ParameterOf(address, "orderId")!)),
                _ => Json(BinanceCalls.AnswerTo(path)),
            });
        }
    }

    private HttpResponseMessage Place(AskedOrder asked, string type)
    {
        var fate = Fate?.Invoke(asked) ?? OrderFate.Taken;

        if (NotTaken(fate) is { } refusal)
        {
            return refusal;
        }

        // An identifier that an order which rests has is taken.
        if (Find(asked.ClientOrderId, PathOf(asked)) is { Rests: true })
        {
            return asked.Market == Market.Spot
                ? Error(HttpStatusCode.BadRequest, -2010, "Duplicate order sent.")
                : Error(HttpStatusCode.BadRequest, -4116, "ClientOrderId is duplicated.");
        }

        // A conditional order of the Futures market whose trigger the price has passed would
        // be triggered at once, and is refused: a stop that sells at or above the price, and a
        // take-profit that sells at or below it, and the other way around for one that buys.
        if (asked is { Market: Market.Futures, Stop: true, Trigger: { } trigger })
        {
            // A stop that sells and a take-profit that buys wait for the price to fall to their trigger.
            var waitsForFall = (asked.Side == "SELL") != asked.TakesProfit;

            if (waitsForFall ? Price <= trigger : Price >= trigger)
            {
                return Error(HttpStatusCode.BadRequest, -2021, "Order would immediately trigger.");
            }
        }

        var kept = new KeptOrder(++_next, asked, type);

        // An order at the market is executed at once, in one execution, and so is an order at
        // a price that the market has reached already. Any other order at a price rests.
        if (type == "MARKET" || (asked.Limit is { } limit && (asked.Side == "BUY" ? limit >= Price : limit <= Price)))
        {
            Fill(kept, asked.Quantity, Price, FeeAsset);
        }

        _orders.Add(kept);

        return fate == OrderFate.TakenUnsaid
            ? throw new TaskCanceledException("Binance does not answer.")
            : Json(Body(kept, full: true));
    }

    /// <summary>Takes a pair: a stop-loss and a take-profit of one list, or neither.</summary>
    private HttpResponseMessage PlacePair(AskedOrder asked)
    {
        var fate = Fate?.Invoke(asked) ?? OrderFate.Taken;

        if (NotTaken(fate) is { } refusal)
        {
            return refusal;
        }

        // An identifier that an order or a pair which rests has is taken.
        if (Find(asked.ClientOrderId, "/api/v3/order") is { Rests: true }
            || Find(asked.TakeProfitId!, "/api/v3/order") is { Rests: true }
            || _orders.Any(order => order.Rests && order.Asked.PairId == asked.PairId))
        {
            return Error(HttpStatusCode.BadRequest, -2010, "Duplicate order sent.");
        }

        // A pair that sells is taken while the price is between its two triggers.
        if (!(asked.TakeProfit > Price && Price > asked.Trigger))
        {
            return Error(HttpStatusCode.BadRequest, -2010, "The relationship of the prices for the orders is not correct.");
        }

        var list = ++_next;
        var stopLoss = new KeptOrder(++_next, asked, "STOP_LOSS") { ListId = list };
        var takeProfit = new KeptOrder(
            ++_next,
            new AskedOrder(asked.Market, asked.TakeProfitId!, Stop: true, asked.Side, asked.Quantity, asked.TakeProfit, ReduceOnly: false, PairId: asked.PairId),
            "TAKE_PROFIT")
        {
            ListId = list,
        };

        stopLoss.Sibling = takeProfit;
        takeProfit.Sibling = stopLoss;
        _orders.Add(stopLoss);
        _orders.Add(takeProfit);

        return fate == OrderFate.TakenUnsaid
            ? throw new TaskCanceledException("Binance does not answer.")
            : Json(ListBody(stopLoss, takeProfit, takenOffAs: null));
    }

    /// <summary>What a call that places something is answered with when Binance does not take it, or null when it does.</summary>
    private static HttpResponseMessage? NotTaken(OrderFate fate) => fate switch
    {
        OrderFate.Refused => Error(HttpStatusCode.BadRequest, -2010, "Order would trigger immediately."),
        OrderFate.Faulted => Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error; unable to process your request. Please try again."),
        OrderFate.Lost => throw new TaskCanceledException("Binance does not answer."),
        _ => null,
    };

    /// <summary>
    /// Gives the symbol isolated margin. Binance answers a symbol that has it already with an
    /// error of its own, which says that nothing is to change.
    /// </summary>
    private HttpResponseMessage Isolate()
    {
        if (Isolated)
        {
            return Error(HttpStatusCode.BadRequest, -4046, "No need to change margin type.");
        }

        Isolated = true;

        return Json(BinanceCalls.MarginSet);
    }

    private HttpResponseMessage Cancel(string identifier, string path)
    {
        if (Find(identifier, path) is not { Rests: true } resting)
        {
            return NoSuchOrder(-2011);
        }

        resting.Status = "CANCELED";

        // Taking one order of a pair off takes the whole pair off.
        if (resting.Sibling is { Rests: true } other)
        {
            other.Status = "CANCELED";
        }

        return resting.Asked is { Market: Market.Futures, Stop: true }
            ? Json($$"""{ "algoId": {{resting.Id}}, "clientAlgoId": "{{identifier}}", "code": "200", "msg": "success" }""")
            : Json(Body(resting, full: false));
    }

    /// <summary>Takes a pair off, by the identifier of the pair: both of its orders, when one of them rests.</summary>
    private HttpResponseMessage CancelPair(string pairId)
    {
        var orders = _orders.Where(order => order.ListId is not null && order.Asked.PairId == pairId).ToList();

        if (!orders.Any(order => order.Rests))
        {
            return NoSuchOrder(-2011);
        }

        foreach (var order in orders.Where(order => order.Rests))
        {
            order.Status = "CANCELED";
        }

        return Json(ListBody(orders.Single(order => !order.TakeProfit), orders.Single(order => order.TakeProfit), takenOffAs: $"off{++_next}"));
    }

    /// <summary>
    /// The order of an identifier, where the call asks for it: Binance keeps the conditional
    /// orders of the Futures market apart, and has none of them at the call of the other orders.
    /// </summary>
    private KeptOrder? Find(string identifier, string path) =>
        _orders.LastOrDefault(order => order.Asked.ClientOrderId == identifier && PathOf(order.Asked) == path);

    /// <summary>The path of the calls that Binance takes an order with, and has it at.</summary>
    private static string PathOf(AskedOrder asked) => asked switch
    {
        { Market: Market.Spot } => "/api/v3/order",
        { Stop: true } => "/fapi/v1/algoOrder",
        _ => "/fapi/v1/order",
    };

    /// <summary>Has a quantity of an order executed at a price, in one execution, with a fee of a tenth of a percent.</summary>
    private void Fill(KeptOrder order, decimal quantity, decimal price, string feeAsset)
    {
        order.Price = order.Executed + quantity > 0m
            ? ((order.Executed * order.Price) + (quantity * price)) / (order.Executed + quantity)
            : price;
        order.Executed += quantity;
        order.Status = order.Executed >= order.Asked.Quantity ? "FILLED" : "PARTIALLY_FILLED";
        order.Trades.Add(feeAsset == BaseAsset
            ? (++_next, quantity, Fee(quantity), BaseAsset)
            : (++_next, quantity, Fee(quantity * price), feeAsset));
    }

    /// <summary>
    /// A pair as Binance writes it: the list, and a report of each of its two orders. A report
    /// of a pair that was taken off names its order as <c>origClientOrderId</c>, beside the
    /// identifier of the call that took it off.
    /// </summary>
    private static string ListBody(KeptOrder stopLoss, KeptOrder takeProfit, string? takenOffAs)
    {
        var done = takenOffAs is not null;

        return $$"""
            { "orderListId": {{stopLoss.ListId}}, "contingencyType": "OCO", "listStatusType": "{{(done ? "ALL_DONE" : "EXEC_STARTED")}}",
              "listOrderStatus": "{{(done ? "ALL_DONE" : "EXECUTING")}}", "listClientOrderId": "{{stopLoss.Asked.PairId}}",
              "transactionTime": {{At}}, "symbol": "BTCUSDT",
              "orders": [
                { "symbol": "BTCUSDT", "orderId": {{takeProfit.Id}}, "clientOrderId": "{{takenOffAs ?? takeProfit.Asked.ClientOrderId}}" },
                { "symbol": "BTCUSDT", "orderId": {{stopLoss.Id}}, "clientOrderId": "{{takenOffAs ?? stopLoss.Asked.ClientOrderId}}" } ],
              "orderReports": [ {{Body(takeProfit, full: false, takenOffAs)}}, {{Body(stopLoss, full: false, takenOffAs)}} ] }
            """;
    }

    /// <summary>An order as Binance writes it: differently on each market, and for a conditional order of the Futures market.</summary>
    private static string Body(KeptOrder order, bool full, string? takenOffAs = null)
    {
        var asked = order.Asked;
        var value = order.Executed * order.Price;

        if (asked.Market == Market.Futures && asked.Stop)
        {
            var status = order.Status == "FILLED" ? "FINISHED" : order.Status;
            var became = order.Became is { } actual
                ? $$""", "actualOrderId": "{{actual}}", "actualPrice": "{{Text(order.Price)}}", "actualQty": "{{Text(order.Executed)}}" """
                : $$""", "actualOrderId": "", "actualPrice": "0.00000", "actualQty": "0" """;

            return $$"""
                { "algoId": {{order.Id}}, "clientAlgoId": "{{asked.ClientOrderId}}", "algoType": "CONDITIONAL", "orderType": "{{order.Type}}",
                  "symbol": "BTCUSDT", "side": "{{asked.Side}}", "positionSide": "BOTH", "timeInForce": "GTC",
                  "quantity": "{{Text(asked.Quantity)}}", "algoStatus": "{{status}}", "triggerPrice": "{{Text(asked.Trigger ?? 0m)}}",
                  "price": "0.00000", "workingType": "{{asked.WorkingType ?? "CONTRACT_PRICE"}}", "closePosition": false, "priceProtect": false,
                  "reduceOnly": {{(asked.ReduceOnly ? "true" : "false")}}, "createTime": {{At}}, "updateTime": {{At}}{{became}} }
                """;
        }

        if (asked.Market == Market.Futures)
        {
            return $$"""
                { "orderId": {{order.Id}}, "symbol": "BTCUSDT", "status": "{{order.Status}}", "clientOrderId": "{{asked.ClientOrderId}}",
                  "price": "{{Text(asked.Limit ?? 0m)}}", "avgPrice": "{{Text(order.Executed > 0m ? order.Price : 0m)}}", "origQty": "{{Text(asked.Quantity)}}",
                  "executedQty": "{{Text(order.Executed)}}", "cumQty": "{{Text(order.Executed)}}", "cumQuote": "{{Text(value)}}",
                  "timeInForce": "GTC", "type": "{{order.Type}}", "reduceOnly": {{(asked.ReduceOnly ? "true" : "false")}}, "side": "{{asked.Side}}",
                  "positionSide": "BOTH", "updateTime": {{At}} }
                """;
        }

        // The full answer of the Spot market to an order that was placed has its executions.
        var fills = full
            ? ", \"fills\": [" + string.Join(", ", order.Trades.Select(trade =>
                $$"""{ "price": "{{Text(order.Price)}}", "qty": "{{Text(trade.Quantity)}}", "commission": "{{Text(trade.Fee)}}", "commissionAsset": "{{trade.FeeAsset}}", "tradeId": {{trade.TradeId}} }""")) + "]"
            : string.Empty;

        // An order that was taken off is named by the identifier that it carried, beside the
        // one of the call that took it off.
        var named = takenOffAs is null
            ? $$""" "clientOrderId": "{{asked.ClientOrderId}}" """
            : $$""" "origClientOrderId": "{{asked.ClientOrderId}}", "clientOrderId": "{{takenOffAs}}" """;

        var trigger = asked.Stop ? $$""", "stopPrice": "{{Text(asked.Trigger ?? 0m)}}" """ : string.Empty;

        return $$"""
            { "symbol": "BTCUSDT", "orderId": {{order.Id}}, "orderListId": {{order.ListId ?? -1}},{{named}},
              "transactTime": {{At}}, "price": "{{Text(asked.Limit ?? 0m)}}", "origQty": "{{Text(asked.Quantity)}}", "executedQty": "{{Text(order.Executed)}}",
              "cummulativeQuoteQty": "{{Text(value)}}", "status": "{{order.Status}}", "timeInForce": "GTC",
              "type": "{{order.Type}}", "side": "{{asked.Side}}"{{trigger}}{{fills}} }
            """;
    }

    /// <summary>The executions of an order, by the number that Binance gave the order, as Binance lists them.</summary>
    private string Trades(string orderId)
    {
        var order = _orders.FirstOrDefault(kept => $"{kept.Became ?? kept.Id}" == orderId || $"{kept.Id}" == orderId);

        return order is null
            ? "[]"
            : "[" + string.Join(", ", order.Trades.Select(trade =>
                $$"""
                { "symbol": "BTCUSDT", "id": {{trade.TradeId}}, "orderId": {{orderId}}, "price": "{{Text(order.Price)}}", "qty": "{{Text(trade.Quantity)}}",
                  "quoteQty": "{{Text(trade.Quantity * order.Price)}}", "commission": "{{Text(trade.Fee)}}", "commissionAsset": "{{trade.FeeAsset}}",
                  "time": {{ExecutedAt.ToUnixTimeMilliseconds()}}, "isBuyer": true, "isMaker": false }
                """)) + "]";
    }

    private static AskedOrder AskedOf(Uri address)
    {
        var path = address.AbsolutePath;
        var side = ParameterOf(address, "side")!;
        var quantity = decimal.Parse(ParameterOf(address, "quantity")!, CultureInfo.InvariantCulture);

        // A pair is asked for as its stop-loss, below the price, with its take-profit above it.
        if (path == PairPath)
        {
            return new AskedOrder(
                Market.Spot,
                ParameterOf(address, "belowClientOrderId")!,
                Stop: true,
                side,
                quantity,
                Amount(address, "belowStopPrice"),
                ReduceOnly: false,
                Limit: null,
                TakeProfit: Amount(address, "aboveStopPrice"),
                TakeProfitId: ParameterOf(address, "aboveClientOrderId"),
                PairId: ParameterOf(address, "listClientOrderId"));
        }

        var type = ParameterOf(address, "type");

        return new AskedOrder(
            path.StartsWith("/fapi/", StringComparison.Ordinal) ? Market.Futures : Market.Spot,
            IdentifierOf(address),
            Stop: type is "STOP_LOSS" or "STOP_MARKET" or "TAKE_PROFIT" or "TAKE_PROFIT_MARKET",
            side,
            quantity,
            Amount(address, "stopPrice") ?? Amount(address, "triggerPrice"),
            ReduceOnly: ParameterOf(address, "reduceOnly") == "true",
            Limit: type == "LIMIT" ? Amount(address, "price") : null,
            Type: type,
            WorkingType: ParameterOf(address, "workingType"));
    }

    /// <summary>The identifier that a call names its order by, under whichever name Binance takes it there.</summary>
    private static string IdentifierOf(Uri address) =>
        ParameterOf(address, "newClientOrderId") ?? ParameterOf(address, "origClientOrderId") ?? ParameterOf(address, "clientAlgoId")
            ?? throw new ArgumentException("The call names no order by its identifier.", nameof(address));

    private static decimal? Amount(Uri address, string name) =>
        ParameterOf(address, name) is { } amount ? decimal.Parse(amount, CultureInfo.InvariantCulture) : null;

    private static string? ParameterOf(Uri address, string name) =>
        address.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == name)
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();

    private static bool IsOrderPath(string path) => path is "/api/v3/order" or "/fapi/v1/order" or "/fapi/v1/algoOrder";

    /// <summary>A tenth of a percent, as the fee of an execution.</summary>
    private static decimal Fee(decimal of) => of / 1000m;

    private static string Text(decimal amount) => amount.ToString("0.00000000", CultureInfo.InvariantCulture);

    private static HttpResponseMessage Json(string body) => StandInSystem.Json(HttpStatusCode.OK, body);

    private static HttpResponseMessage NoSuchOrder(int code) =>
        Error(HttpStatusCode.BadRequest, code, code == -2013 ? "Order does not exist." : "Unknown order sent.");

    private static HttpResponseMessage Error(HttpStatusCode status, int code, string message) =>
        StandInSystem.Json(status, $$"""{ "code": {{code}}, "msg": "{{message}}" }""");
}

/// <summary>
/// Stands for the watch on the clock of the exchange: new entries are paused where the test says
/// so, and nowhere else. The application of a test reads no clock of Binance, so its own watch
/// would pause them everywhere.
/// </summary>
internal sealed class StandInEntries : IExchangeClock
{
    /// <summary>Whether new entries are paused, everywhere.</summary>
    public bool Paused { get; set; }

    public IReadOnlyList<ExchangeClockDrift> Drift() => [];

    public bool EntriesPaused(Market market, TradingMode mode) => Paused;
}

/// <summary>
/// The position of BTCUSDT that an account holds on the Futures market, as the exchange says it
/// with its prices: the one that the plans of the tests open, entered at 60000 for 0.02.
/// </summary>
internal static class HeldPositions
{
    /// <summary>A long position, at a mark price, with the price that the exchange liquidates it at, or with none.</summary>
    public static ExchangeFuturesPosition Long(decimal mark, decimal? liquidation = 50000m) =>
        new(ExchangeCallOutcome.Succeeded, 0.02m, EntryPrice: 60000m, mark, liquidation);

    /// <summary>A short position, at a mark price, with the price that the exchange liquidates it at, or with none.</summary>
    public static ExchangeFuturesPosition Short(decimal mark, decimal? liquidation = 70000m) =>
        new(ExchangeCallOutcome.Succeeded, -0.02m, EntryPrice: 60000m, mark, liquidation);
}

/// <summary>
/// Stands before the market data of the gateway, for the mark price of a symbol alone, once a
/// test says when the next funding is: everything else, and the mark price until then, is the
/// gateway's own. The gateway holds a mark price that it read for a second of real time, whatever
/// the clock of a test shows, so a test that moves its clock over a funding time would be told
/// the funding time that has passed.
/// </summary>
internal sealed class StandInFundingTimes : IExchangeMarketData
{
    private IExchangeMarketData? _gateway;
    private int _asked;

    /// <summary>When the next funding is, as the exchange says it with the mark price, or null where the gateway's own answer is given.</summary>
    public DateTimeOffset? Next { get; set; }

    /// <summary>How a read of the mark price ends, once <see cref="Next"/> is said.</summary>
    public ExchangeCallOutcome Outcome { get; set; } = ExchangeCallOutcome.Succeeded;

    /// <summary>How many times the mark price was asked for.</summary>
    public int Asked => Volatile.Read(ref _asked);

    /// <summary>Puts it before the market data of the gateway.</summary>
    public StandInFundingTimes Before(IExchangeMarketData gateway)
    {
        _gateway = gateway;

        return this;
    }

    public Task<ExchangeMarkPrice> GetMarkPriceAsync(TradingMode mode, string symbol, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _asked);

        if (Next is not { } next)
        {
            return Gateway.GetMarkPriceAsync(mode, symbol, cancellationToken);
        }

        return Task.FromResult(Outcome == ExchangeCallOutcome.Succeeded
            ? new ExchangeMarkPrice(Outcome, 60000m, 0.0001m, next, DateTimeOffset.UtcNow)
            : new ExchangeMarkPrice(Outcome, null, null, null, null));
    }

    public Task<ExchangeCandles> GetCandlesAsync(
        Market market, TradingMode mode, string symbol, CandleInterval interval, CancellationToken cancellationToken) =>
        Gateway.GetCandlesAsync(market, mode, symbol, interval, cancellationToken);

    public Task<ExchangePrice> GetPriceAsync(Market market, TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        Gateway.GetPriceAsync(market, mode, symbol, cancellationToken);

    public Task<ExchangeSymbols> GetSymbolsAsync(Market market, TradingMode mode, CancellationToken cancellationToken) =>
        Gateway.GetSymbolsAsync(market, mode, cancellationToken);

    public Task<ExchangeOpenInterest> GetOpenInterestAsync(TradingMode mode, string symbol, CancellationToken cancellationToken) =>
        Gateway.GetOpenInterestAsync(mode, symbol, cancellationToken);

    private IExchangeMarketData Gateway => _gateway ?? throw new InvalidOperationException("It stands before the market data of an application.");
}

/// <summary>
/// The application with a <see cref="TradingBinance"/> behind the gateway, on a database of the
/// test's own and a clock that only the test moves, for a test of the orders: a user with a
/// Binance credential and an agent that runs, and what places and settles the orders of it.
/// </summary>
internal sealed class OrdersApplication : IDisposable
{
    /// <summary>What the clock of the application shows as it starts.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 17, 10, 15, 30, TimeSpan.Zero);

    /// <param name="settlesOrders">
    /// Whether the scheduler of the application runs the job that settles the orders, as it does
    /// outside the tests. Without it a test runs the job itself, at a moment of its choosing.
    /// </param>
    /// <param name="database">A database that an earlier application of the test has used.</param>
    /// <param name="binance">The Binance that an earlier application of the test has used, with the orders that it has.</param>
    public OrdersApplication(bool settlesOrders = false, TestDatabase? database = null, TradingBinance? binance = null)
    {
        Binance = binance ?? new TradingBinance();
        Application = new AccountsApplication(
            clock: Clock,
            database: database,
            host: host => host.ConfigureServices(services =>
            {
                services.AddHttpClient(ExchangeGatewayModule.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Binance.System);
                services.AddSingleton<IExchangeClock>(Entries);

                // The market data of the gateway, with the funding times of the test before it.
                var registered = services.Last(service => service.ServiceType == typeof(IExchangeMarketData));
                var gateway = registered.ImplementationType!;

                services.Remove(registered);
                services.AddSingleton(gateway);
                services.AddSingleton<IExchangeMarketData>(
                    provider => FundingTimes.Before((IExchangeMarketData)provider.GetRequiredService(gateway)));
            }),
            settlesOrders: settlesOrders);
    }

    public AccountsApplication Application { get; }

    public TestClock Clock { get; } = new(Start);

    public TradingBinance Binance { get; }

    public StandInEntries Entries { get; } = new();

    /// <summary>When the next funding is, where a test says it: the mark price is the gateway's own until then.</summary>
    public StandInFundingTimes FundingTimes { get; } = new();

    public IPlanOrders Orders => Application.PlanOrders;

    /// <summary>
    /// Gives a new user a Binance credential for Demo and an agent of BTCUSDT on a market, in a
    /// state, and gives the agent as it is stored.
    /// </summary>
    public async Task<Agent> GiveAgentAsync(
        string email = "ada@example.com", Market market = Market.Spot, AgentState state = AgentState.Running)
    {
        var user = await Application.RegisterVerifiedAsync(email);
        await Application.GiveKeysAsync(user.Id, Clock.GetUtcNow());

        var id = await Application.GiveAgentAsync(
            user.Id, Clock.GetUtcNow(), state: state, market: market, cause: AgentStateCause.UserStarted, team: false);

        return await AgentAsync(id);
    }

    /// <summary>An agent as it is stored now.</summary>
    public async Task<Agent> AgentAsync(Guid agentId)
    {
        await using var context = await Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Agent>().AsNoTracking().SingleAsync(agent => agent.Id == agentId, TestContext.Current.CancellationToken);
    }

    /// <summary>What a cycle of an agent is handed as it starts, for a close a moment ago.</summary>
    public CycleStart CycleOf(Agent agent) =>
        new(Guid.CreateVersion7(), agent, [], Clock.GetUtcNow().AddSeconds(-30), new ExchangeCandles(ExchangeCallOutcome.Failed, [], null));

    /// <summary>
    /// Places the orders of a plan that the Risk Guard accepts, as a cycle of the agent does,
    /// and moves the clock on while that waits for a moment of it.
    /// </summary>
    public Task PlaceAsync(Agent agent, GuardCase plan, CancellationToken? stopping = null) =>
        PlaceAsync(CycleOf(agent), plan, stopping);

    /// <summary>The same, for a cycle that the test made.</summary>
    public Task PlaceAsync(CycleStart cycle, GuardCase plan, CancellationToken? stopping = null)
    {
        var accepted = GuardCheck.Of(plan);
        Assert.True(accepted.IsAccepted, "The plan of the test is one that the Risk Guard accepts.");

        return WhileTimePassesAsync(Orders.PlaceAsync(cycle, accepted, stopping ?? CancellationToken.None));
    }

    /// <summary>Settles the orders of an agent with the exchange, and moves the clock on while that waits.</summary>
    public Task SettleAsync(Guid agentId) => WhileTimePassesAsync(Orders.SettleAsync(agentId, CancellationToken.None));

    /// <summary>Closes the position of an agent at market, as for its user, and moves the clock on while that waits.</summary>
    public async Task<PositionCloseOutcome> CloseAsync(Guid agentId)
    {
        var closing = Orders.CloseAtMarketAsync(agentId, CancellationToken.None);
        await WhileTimePassesAsync(closing);

        return await closing;
    }

    /// <summary>
    /// Waits for something that the application does, and moves its clock on by a second at a
    /// time while it is not over: what waits for a moment of the clock then sees it come.
    /// </summary>
    public async Task WhileTimePassesAsync(Task doing)
    {
        var patience = TimeSpan.FromSeconds(30);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();

        while (!doing.IsCompleted)
        {
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(began) < patience, "What the test waits for did not end.");

            await Task.WhenAny(doing, Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken));

            if (!doing.IsCompleted)
            {
                Clock.Set(Clock.GetUtcNow().AddSeconds(1));
            }
        }

        await doing;
    }

    /// <summary>The orders of an agent as they are stored, in the order they were written in, which is that of their ids.</summary>
    public async Task<IReadOnlyList<Order>> OrdersOfAsync(Guid agentId)
    {
        await using var context = await Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Order>()
            .AsNoTracking()
            .Where(order => order.AgentId == agentId)
            .OrderBy(order => order.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The positions of an agent as they are stored, in the order they were opened in.</summary>
    public async Task<IReadOnlyList<Position>> PositionsOfAsync(Guid agentId)
    {
        await using var context = await Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Position>()
            .AsNoTracking()
            .Where(position => position.AgentId == agentId)
            .OrderBy(position => position.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The executions that are stored for an order.</summary>
    public async Task<IReadOnlyList<Fill>> FillsOfAsync(Guid orderId)
    {
        await using var context = await Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Fill>().AsNoTracking().Where(fill => fill.OrderId == orderId).ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Watches the positions of the Futures market, as the job that the scheduler runs at the
    /// time between reconciliations does, at the moment that the clock shows.
    /// </summary>
    public Task WatchAsync() => Application.WatchPositionsAsync();

    /// <summary>What is stored of what a position received and paid as funding, oldest first.</summary>
    public async Task<IReadOnlyList<Funding>> FundingsOfAsync(Guid positionId)
    {
        await using var context = await Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Funding>()
            .AsNoTracking()
            .Where(funding => funding.PositionId == positionId)
            .OrderBy(funding => funding.At)
            .ThenBy(funding => funding.TransactionId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The notifications of a user as they are stored, in the order they were raised in.</summary>
    public async Task<IReadOnlyList<Notification>> NotificationsOfAsync(Guid userId)
    {
        await using var context = await Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Notification>()
            .AsNoTracking()
            .Where(notification => notification.UserId == userId)
            .OrderBy(notification => notification.CreatedAt)
            .ThenBy(notification => notification.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose() => Application.Dispose();
}
