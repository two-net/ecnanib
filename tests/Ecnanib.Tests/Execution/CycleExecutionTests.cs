using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.DecisionCycle;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-CYC-02 step 9: an Autonomous agent's cycle executes what the Risk Engine sized, and records how it went. A
/// Confirm agent's waits for approval (#38). AC-01: every order on record cites a passing Risk Engine evaluation.
/// </summary>
public sealed class CycleExecutionTests(PostgresFixture postgres)
{
    /// <summary>
    /// The audit query AC-01 asks for, over the decision traces and the orders: an order from the council cites its
    /// cycle's passing evaluation, and one of Ecnanib's own carries its own. It counts the orders that do neither.
    /// </summary>
    public const string Ac01 = """
        SELECT count(*) FROM orders o
        WHERE NOT (
            (o.origin = 'Council' AND EXISTS (SELECT 1 FROM risk_evaluations e WHERE e.cycle_id = o.cycle_id AND e.verdict = 'Pass'))
            OR (o.origin = 'System' AND o.system_evaluation ->> 'verdict' = 'Pass'))
        """;

    private const string FuturesLong = """
        {"action":"OPEN_LONG","side":"long","entry_type":"market","entry_price":null,"stop_price":60115,
         "target_prices":[60150,60135],"risk_percent":1,"limit_ttl_candles":null,"close_fraction":null,
         "invalidation":"A close below 60115.","rationale":"Momentum above the 20 EMA.","confidence":0.6}
        """;

    private const string Sell = """
        {"action":"SELL","side":null,"entry_type":null,"entry_price":null,"stop_price":null,"target_prices":[],
         "risk_percent":null,"limit_ttl_candles":null,"close_fraction":null,"invalidation":null,
         "rationale":"Momentum has faded.","confidence":0.6}
        """;

    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task An_Autonomous_agents_cycle_executes_its_entry_and_every_order_cites_a_passing_evaluation()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { ExecutionMode = ExecutionMode.Autonomous });

        await app.RunAsync(user, agent);

        Assert.Equal(["Executed"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["MARKET", "OCO"], book.Placed.Select(sent => sent.Type));
        Assert.Equal(["Protected"], await ColumnAsync(app, $"SELECT execution ->> 'status' FROM decision_cycles WHERE agent_id = '{agent.Id}'"));
        Assert.Equal(3L, await ScalarAsync(app, $"SELECT count(*) FROM orders WHERE agent_id = '{agent.Id}'"));
        Assert.Equal(0L, await ScalarAsync(app, Ac01));
        Assert.Contains("TradeOpened", await ColumnAsync(app, $"SELECT type FROM notifications WHERE user_id = '{user.Id}'"));
    }

    [Fact]
    public async Task A_close_Ecnanib_makes_on_its_own_carries_its_own_passing_evaluation()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        book.Fail(HttpMethod.Post, FakeOrderBook.SpotList, Fault.Rejected(-2010, "Order would trigger immediately."));
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { ExecutionMode = ExecutionMode.Autonomous });

        await app.RunAsync(user, agent);

        Assert.Equal(["Executed"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["Closed"], await ColumnAsync(app, $"SELECT execution ->> 'status' FROM decision_cycles WHERE agent_id = '{agent.Id}'"));
        Assert.Equal(1L, await ScalarAsync(app, $"SELECT count(*) FROM orders WHERE agent_id = '{agent.Id}' AND origin = 'System'"));
        Assert.Equal(0L, await ScalarAsync(app, Ac01));
    }

    [Fact]
    public async Task A_Confirm_agents_cycle_sends_nothing_and_waits_for_approval()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["ReadyToExecute"], await app.CyclesAsync(agent.Id));
        Assert.Empty(book.Placed);
        Assert.Equal(0L, await ScalarAsync(app, $"SELECT count(*) FROM orders WHERE agent_id = '{agent.Id}'"));
    }

    [Fact]
    public async Task A_Futures_account_only_its_user_can_fix_puts_the_agent_in_Error_at_once()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = 60_120.20m };
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(FuturesLong);
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { Market = Market.Futures, ExecutionMode = ExecutionMode.Autonomous });
        app.Agents.Binance.On(HttpMethod.Get, AgentApp.FuturesAccountConfigPath, _ => FakeBinance.Json(BinanceSamples.FuturesAccountConfig(hedgeMode: true)));

        await app.RunAsync(user, agent);

        Assert.Equal(["ExecutionFailed Execution"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["HedgeMode"], await ColumnAsync(app, $"SELECT execution ->> 'futuresSetup' FROM decision_cycles WHERE agent_id = '{agent.Id}'"));
        Assert.Empty(book.Placed);
        Assert.Equal(AgentStatus.Error, await app.Agents.StatusAsync(user.Id, agent.Id));
        Assert.Contains("AgentPausedErrors", await ColumnAsync(app, $"SELECT type FROM notifications WHERE user_id = '{user.Id}'"));
    }

    [Fact]
    public async Task A_Futures_cycle_sets_the_symbol_up_and_executes_with_a_reduce_only_stop()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = 60_120.20m };
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(FuturesLong);
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { Market = Market.Futures, ExecutionMode = ExecutionMode.Autonomous });

        await app.RunAsync(user, agent);

        Assert.Equal(["Executed"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["MARKET", "STOP_MARKET", "TAKE_PROFIT_MARKET"], book.Placed.Select(sent => sent.Type));
        Assert.Single(app.Agents.Binance.To("/fapi/v1/symbolConfig"));
        Assert.Equal(0L, await ScalarAsync(app, Ac01));
    }

    [Fact]
    public async Task The_next_cycle_shows_the_position_to_the_Strategist_and_Risk_Critic_and_executes_their_exit()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { ExecutionMode = ExecutionMode.Autonomous });
        await app.RunAsync(user, agent);

        app.Clock.Advance(TimeSpan.FromHours(1));
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(Sell);
        await app.RunAsync(user, agent);

        Assert.Equal(["Executed", "Executed"], await app.CyclesAsync(agent.Id));
        Assert.Equal(
            ["Protected", "Exited"],
            await ColumnAsync(app, $"SELECT execution ->> 'status' FROM decision_cycles WHERE agent_id = '{agent.Id}' ORDER BY candle_close"));
        var strategist = await app.CallColumnAsync(agent.Id, AiRole.Strategist, "message");
        Assert.Contains("<position>\nnone\n</position>", strategist[0], StringComparison.Ordinal);
        Assert.Contains("<position side=\"Long\">", strategist[1], StringComparison.Ordinal);
        Assert.Contains("stop: 60118\n", strategist[1], StringComparison.Ordinal);
        Assert.Contains("targets: 60135\n", strategist[1], StringComparison.Ordinal);
        Assert.Contains("<position side=\"Long\">", (await app.CallColumnAsync(agent.Id, AiRole.RiskCritic, "message"))[^1], StringComparison.Ordinal);
        Assert.Single(book.SentTo(FakeOrderBook.SpotOrder), sent => sent.Side == "SELL");
        Assert.Equal(0L, await ScalarAsync(app, Ac01));
        Assert.Equal(1L, await ScalarAsync(app, $"SELECT count(*) FROM orders WHERE agent_id = '{agent.Id}' AND leg = 'Exit' AND origin = 'Council'"));
    }

    private static Task<List<string>> ColumnAsync(CycleApp app, string sql) => Sql.ColumnAsync(app.ConnectionString, sql, Cancellation);

    private static Task<long> ScalarAsync(CycleApp app, string sql) => Sql.ScalarAsync<long>(app.ConnectionString, sql, Cancellation);
}
