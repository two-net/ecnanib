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
    /// opened, and the stop-loss that closed it. Everything is over, so the agent holds nothing,
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
            alertedAt: null);

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
            checkedAt: at);

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
            checkedAt: at.AddMinutes(30));

        // The position first: its orders name it.
        context.Add(position);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Add(entry);
        context.Add(stopLoss);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Add(new Fill(Guid.CreateVersion7(), entryId, userId, tradeId: "9001", 100_000m, 0.01m, fee: 0.5m, feeAsset: "USDT", at));
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
/// <param name="ClientOrderId">The identifier that the order carries.</param>
/// <param name="Stop">Whether it is a stop.</param>
/// <param name="Side">Binance's name of the side.</param>
/// <param name="Quantity">Its quantity.</param>
/// <param name="Trigger">The price that a stop is triggered at.</param>
/// <param name="ReduceOnly">Whether it was sent as an order that only reduces a position.</param>
internal sealed record AskedOrder(
    Market Market, string ClientOrderId, bool Stop, string Side, decimal Quantity, decimal? Trigger, bool ReduceOnly);

/// <summary>An order that a <see cref="TradingBinance"/> has.</summary>
internal sealed class KeptOrder(long id, AskedOrder asked)
{
    public long Id { get; } = id;

    public AskedOrder Asked { get; } = asked;

    /// <summary>Binance's word for its state: <c>NEW</c>, <c>FILLED</c>, <c>CANCELED</c>, or <c>EXPIRED</c>.</summary>
    public string Status { get; set; } = "NEW";

    public decimal Executed { get; set; }

    public decimal Price { get; set; }

    /// <summary>The id of the order that a conditional order of the Futures market became when it was triggered.</summary>
    public long? Became { get; set; }

    /// <summary>The executions: the number of each, its quantity, its fee, and the asset of the fee.</summary>
    public List<(long TradeId, decimal Quantity, decimal Fee, string FeeAsset)> Trades { get; } = [];
}

/// <summary>
/// A Binance of the tests that keeps orders: it takes an order that is placed, answers what
/// became of it, takes it off, and lists its executions, in the form that Binance's
/// documentation gives. An order at the market is executed at once, at <see cref="Price"/>, and a
/// stop rests until the test has it executed. A test says what becomes of a call that places an
/// order with <see cref="Fate"/>, and of any other call with <see cref="Instead"/>. Every other
/// call of the gateway is answered as <see cref="BinanceCalls.AnswerTo"/> answers it.
/// </summary>
internal sealed class TradingBinance
{
    private const string BaseAsset = "BTC";
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

    /// <summary>What becomes of a call that places an order, when it is not taken and answered.</summary>
    public Func<AskedOrder, OrderFate>? Fate { get; set; }

    /// <summary>What a call is answered with in place of everything else, when this gives an answer. It may fail, as a connection does.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Instead { get; set; }

    /// <summary>The orders that Binance has, in the order it took them.</summary>
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

    /// <summary>The calls that placed an order, in the order they came: each as the order it asked for.</summary>
    public IReadOnlyList<AskedOrder> Asked =>
        [.. System.Calls.Where(call => call.Method == "POST").Select(call => AskedOf(call.Address))];

    /// <summary>The calls that asked what became of an order, by the identifier each asked with, in the order they came.</summary>
    public IReadOnlyList<string> AskedAbout =>
        [
            .. System.Calls
                .Where(call => call.Method == "GET" && IsOrderPath(call.Address.AbsolutePath))
                .Select(call => IdentifierOf(call.Address)),
        ];

    /// <summary>The calls about orders, each as its method and its path, in the order they came.</summary>
    public IReadOnlyList<string> OrderCalls =>
        [
            .. System.Calls
                .Where(call => IsOrderPath(call.Address.AbsolutePath) || call.Address.AbsolutePath.EndsWith("Trades", StringComparison.Ordinal))
                .Select(call => $"{call.Method} {call.Address.AbsolutePath}"),
        ];

    /// <summary>The one order that Binance has of a kind.</summary>
    public KeptOrder The(bool stop) => Assert.Single(Orders, order => order.Asked.Stop == stop);

    /// <summary>Has a stop that rests executed in full, at its trigger, as when the price reaches it.</summary>
    public void Execute(KeptOrder order)
    {
        lock (_gate)
        {
            order.Status = "FILLED";
            order.Executed = order.Asked.Quantity;
            order.Price = order.Asked.Trigger ?? Price;
            order.Became = order.Asked.Market == Market.Futures && order.Asked.Stop ? ++_next : null;
            order.Trades.Add((++_next, order.Executed, Fee(order.Executed * order.Price), "USDT"));
        }
    }

    /// <summary>Takes an order off the exchange, as its user does at Binance.</summary>
    public void TakeOff(KeptOrder order)
    {
        lock (_gate)
        {
            order.Status = "CANCELED";
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
                ("POST", _) when IsOrderPath(path) => Place(AskedOf(address)),
                ("GET", _) when IsOrderPath(path) => Find(IdentifierOf(address)) is { } found ? Json(Body(found, full: false)) : NoSuchOrder(-2013),
                ("DELETE", _) when IsOrderPath(path) => Cancel(IdentifierOf(address), market),
                ("GET", "/api/v3/myTrades" or "/fapi/v1/userTrades") => Json(Trades(ParameterOf(address, "orderId")!)),
                _ => Json(BinanceCalls.AnswerTo(path)),
            });
        }
    }

    private HttpResponseMessage Place(AskedOrder asked)
    {
        var fate = Fate?.Invoke(asked) ?? OrderFate.Taken;

        switch (fate)
        {
            case OrderFate.Refused:
                return Error(HttpStatusCode.BadRequest, -2010, "Order would trigger immediately.");

            case OrderFate.Faulted:
                return Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error; unable to process your request. Please try again.");

            case OrderFate.Lost:
                throw new TaskCanceledException("Binance does not answer.");
        }

        // An identifier that an order which rests has is taken.
        if (Find(asked.ClientOrderId) is { Status: "NEW" })
        {
            return asked.Market == Market.Spot
                ? Error(HttpStatusCode.BadRequest, -2010, "Duplicate order sent.")
                : Error(HttpStatusCode.BadRequest, -4116, "ClientOrderId is duplicated.");
        }

        var kept = new KeptOrder(++_next, asked);

        if (!asked.Stop)
        {
            // An order at the market is executed at once, in one execution.
            kept.Status = "FILLED";
            kept.Executed = asked.Quantity;
            kept.Price = Price;
            kept.Trades.Add(FeeAsset == BaseAsset
                ? (++_next, asked.Quantity, Fee(asked.Quantity), BaseAsset)
                : (++_next, asked.Quantity, Fee(asked.Quantity * Price), FeeAsset));
        }

        _orders.Add(kept);

        return fate == OrderFate.TakenUnsaid
            ? throw new TaskCanceledException("Binance does not answer.")
            : Json(Body(kept, full: true));
    }

    private HttpResponseMessage Cancel(string identifier, Market market)
    {
        if (Find(identifier) is not { Status: "NEW" } resting)
        {
            return NoSuchOrder(-2011);
        }

        resting.Status = "CANCELED";

        return market == Market.Futures && resting.Asked.Stop
            ? Json($$"""{ "algoId": {{resting.Id}}, "clientAlgoId": "{{identifier}}", "code": "200", "msg": "success" }""")
            : Json(Body(resting, full: false));
    }

    private KeptOrder? Find(string identifier) => _orders.LastOrDefault(order => order.Asked.ClientOrderId == identifier);

    /// <summary>An order as Binance writes it: differently on each market, and for a conditional order of the Futures market.</summary>
    private static string Body(KeptOrder order, bool full)
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
                { "algoId": {{order.Id}}, "clientAlgoId": "{{asked.ClientOrderId}}", "algoType": "CONDITIONAL", "orderType": "STOP_MARKET",
                  "symbol": "BTCUSDT", "side": "{{asked.Side}}", "positionSide": "BOTH", "timeInForce": "GTC",
                  "quantity": "{{Text(asked.Quantity)}}", "algoStatus": "{{status}}", "triggerPrice": "{{Text(asked.Trigger ?? 0m)}}",
                  "price": "0.00000", "workingType": "CONTRACT_PRICE", "closePosition": false, "priceProtect": false,
                  "reduceOnly": {{(asked.ReduceOnly ? "true" : "false")}}, "createTime": {{At}}, "updateTime": {{At}}{{became}} }
                """;
        }

        if (asked.Market == Market.Futures)
        {
            return $$"""
                { "orderId": {{order.Id}}, "symbol": "BTCUSDT", "status": "{{order.Status}}", "clientOrderId": "{{asked.ClientOrderId}}",
                  "price": "0.00", "avgPrice": "{{Text(order.Executed > 0m ? order.Price : 0m)}}", "origQty": "{{Text(asked.Quantity)}}",
                  "executedQty": "{{Text(order.Executed)}}", "cumQty": "{{Text(order.Executed)}}", "cumQuote": "{{Text(value)}}",
                  "timeInForce": "GTC", "type": "MARKET", "reduceOnly": {{(asked.ReduceOnly ? "true" : "false")}}, "side": "{{asked.Side}}",
                  "positionSide": "BOTH", "updateTime": {{At}} }
                """;
        }

        // The full answer of the Spot market to an order that was placed has its executions.
        var fills = full
            ? ", \"fills\": [" + string.Join(", ", order.Trades.Select(trade =>
                $$"""{ "price": "{{Text(order.Price)}}", "qty": "{{Text(trade.Quantity)}}", "commission": "{{Text(trade.Fee)}}", "commissionAsset": "{{trade.FeeAsset}}", "tradeId": {{trade.TradeId}} }""")) + "]"
            : string.Empty;

        return $$"""
            { "symbol": "BTCUSDT", "orderId": {{order.Id}}, "orderListId": -1, "clientOrderId": "{{asked.ClientOrderId}}",
              "transactTime": {{At}}, "price": "0.00000000", "origQty": "{{Text(asked.Quantity)}}", "executedQty": "{{Text(order.Executed)}}",
              "cummulativeQuoteQty": "{{Text(value)}}", "status": "{{order.Status}}", "timeInForce": "GTC",
              "type": "{{(asked.Stop ? "STOP_LOSS" : "MARKET")}}", "side": "{{asked.Side}}"{{fills}} }
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
                  "time": {{At}}, "isBuyer": true, "isMaker": false }
                """)) + "]";
    }

    private static AskedOrder AskedOf(Uri address)
    {
        var path = address.AbsolutePath;
        var type = ParameterOf(address, "type");

        return new AskedOrder(
            path.StartsWith("/fapi/", StringComparison.Ordinal) ? Market.Futures : Market.Spot,
            IdentifierOf(address),
            Stop: type is "STOP_LOSS" or "STOP_MARKET",
            ParameterOf(address, "side")!,
            decimal.Parse(ParameterOf(address, "quantity")!, CultureInfo.InvariantCulture),
            (ParameterOf(address, "stopPrice") ?? ParameterOf(address, "triggerPrice")) is { } trigger
                ? decimal.Parse(trigger, CultureInfo.InvariantCulture)
                : null,
            ReduceOnly: ParameterOf(address, "reduceOnly") == "true");
    }

    /// <summary>The identifier that a call names its order by, under whichever name Binance takes it there.</summary>
    private static string IdentifierOf(Uri address) =>
        ParameterOf(address, "newClientOrderId") ?? ParameterOf(address, "origClientOrderId") ?? ParameterOf(address, "clientAlgoId")
            ?? throw new ArgumentException("The call names no order by its identifier.", nameof(address));

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
            }),
            settlesOrders: settlesOrders);
    }

    public AccountsApplication Application { get; }

    public TestClock Clock { get; } = new(Start);

    public TradingBinance Binance { get; }

    public StandInEntries Entries { get; } = new();

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
