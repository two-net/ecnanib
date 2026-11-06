using System.Globalization;
using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-AGT-07, FR-AGT-08, UC-05: the kill switch, end to end. Once the agent is stopped by it, execution cancels its open
/// orders and closes what it holds at market, each close under an evaluation of its own (AC-01), and never touches the
/// user's own orders (FR-AGT-09). What can't be done at once, reconciliation finishes, which is also what finishes a kill
/// switch cut off by a restart. An agent paused or stopped while its cycle ran sends nothing.
/// </summary>
public sealed class KillSwitchTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExecutionApp.Cancellation;

    [Fact]
    public async Task A_Futures_position_is_closed_reduce_only_at_market_and_its_stop_and_target_cancelled()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        app.Book.SeedAlgo("my-own-stop", "SELL", "STOP_MARKET", 0.5m, 50_000m, finished: false);

        var result = await app.Agents.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.Closed, result.Outcome);
        Assert.Equal((AgentStatus.Stopped, AgentStatusReason.KillSwitch), (result.Agent!.Status, result.Agent.StatusReason));
        Assert.Equal([$"Closed {Q(placed.Quantity)} 59800 60400 KillSwitch"], await app.PartsAsync(agent.Id));
        var close = Assert.Single(app.Book.SentTo(FakeOrderBook.FuturesOrder), sent => sent.Side == "SELL");
        Assert.Equal((placed.Quantity, true), (close.Quantity, close.ReduceOnly));
        Assert.Equal("CANCELED", app.Book.Find(placed.Stop)!.Status);
        Assert.Equal("CANCELED", app.Book.Find(placed.Target)!.Status);
        Assert.Equal(0m, app.Book.FuturesAmount);

        // The user's own order isn't the agent's to touch (FR-AGT-09).
        Assert.Equal("NEW", app.Book.Find("my-own-stop")!.Status);

        // AC-01: Ecnanib's own close carries its own passing evaluation.
        Assert.Equal(1L, await app.CountAsync(agent.Id, "leg = 'EmergencyClose' AND origin = 'System' AND status = 'Filled' AND system_evaluation ->> 'verdict' = 'Pass'"));
        Assert.Equal(0L, await app.CountAsync(agent.Id, "origin = 'System' AND (system_evaluation ->> 'verdict') IS DISTINCT FROM 'Pass'"));

        Assert.Equal(["TradeOpened", "KillSwitchExecuted", "TradeClosed"], await app.NotificationsAsync(user.Id));
        Assert.StartsWith("Closed KillSwitch", Assert.Single(await app.TradesAsync(agent.Id)), StringComparison.Ordinal);
        Assert.False(await HasOpenAsync(app, user.Id, agent.Id));
    }

    [Fact]
    public async Task A_Spot_OCO_list_is_cancelled_whole_and_only_what_the_agents_fills_bought_is_sold()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        app.Book.SpotBaseOwned = 1m;

        var result = await app.Agents.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.Closed, result.Outcome);
        Assert.Equal(["Closed 0.01664 59800 60400 KillSwitch"], await app.PartsAsync(agent.Id));
        Assert.All(app.Book.Orders.Where(order => order.ListId is not null), order => Assert.Equal("CANCELED", order.Status));
        var sell = Assert.Single(app.Book.SentTo(FakeOrderBook.SpotOrder), sent => sent.Side == "SELL");
        Assert.Equal((0.01664m, false), (sell.Quantity, sell.ReduceOnly));

        // The user's own BTC is still there, with the dust Binance's fee left of the agent's.
        Assert.InRange(app.Book.SpotBase, 1m, 1.0001m);
    }

    [Fact]
    public async Task An_unfilled_limit_entry_is_cancelled_and_what_it_filled_is_closed()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, EntryType.Limit, limit: 59_950m), ttl: 5));
        var entry = Assert.Single(app.Book.Orders, order => order is { Futures: true, Algo: false, Type: "LIMIT" });
        app.Book.Fill(entry.ClientOrderId, 0.002m);

        var result = await app.Agents.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.Closed, result.Outcome);
        Assert.Equal("CANCELED", entry.Status);
        Assert.Equal(["Closed 0.002 59800 60400 KillSwitch"], await app.PartsAsync(agent.Id));
        Assert.Equal(0m, app.Book.FuturesAmount);

        // What filled was closed rather than given a stop.
        Assert.DoesNotContain(app.Book.Orders, order => order.Type == "STOP_MARKET");
    }

    [Fact]
    public async Task An_agent_with_nothing_open_is_only_stopped()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();

        var result = await app.Agents.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.NothingOpen, result.Outcome);
        Assert.Equal(AgentStatus.Stopped, result.Agent!.Status);
        Assert.Empty(app.Book.Placed);
    }

    /// <summary>
    /// A stop is never cancelled on records Binance hasn't confirmed: it stays, the user is told, and the reconcile sweep
    /// closes the position once Binance answers (NFR-REL-02).
    /// </summary>
    [Fact]
    public async Task When_Binance_cant_confirm_the_orders_the_stop_stays_and_the_sweep_closes_it_later()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        app.Book.Fail(HttpMethod.Get, FakeOrderBook.FuturesOpenAlgoOrders, Fault.ServerError(accepted: false));

        var result = await app.Agents.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.Unconfirmed, result.Outcome);
        Assert.Equal(AgentStatus.Stopped, result.Agent!.Status);
        Assert.Equal("NEW", app.Book.Find(placed.Stop)!.Status);
        Assert.Equal(placed.Quantity, app.Book.FuturesAmount);

        await app.ReconcileAsync();

        Assert.Equal([$"Closed {Q(placed.Quantity)} 59800 60400 KillSwitch"], await app.PartsAsync(agent.Id));
        Assert.Equal(0m, app.Book.FuturesAmount);
    }

    /// <summary>The status is the request to close, so a kill switch cut off by a restart is finished by the next sweep.</summary>
    [Fact]
    public async Task An_agent_stopped_by_the_kill_switch_is_closed_by_the_reconcile_sweep()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        await app.SetStatusAsync(agent.Id, AgentStatus.Stopped, AgentStatusReason.KillSwitch);

        await app.ReconcileAsync();

        Assert.Equal(["Closed 0.01664 59800 60400 KillSwitch"], await app.PartsAsync(agent.Id));
        Assert.False(await HasOpenAsync(app, user.Id, agent.Id));
    }

    /// <summary>
    /// A stopped agent's stop cancelled outside Ecnanib is the user's (FR-AGT-07), unless the kill switch stopped it, which
    /// closes what the stop guarded.
    /// </summary>
    [Fact]
    public async Task A_stop_cancelled_outside_Ecnanib_after_the_kill_switch_is_closed_not_left_to_the_user()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        await app.SetStatusAsync(agent.Id, AgentStatus.Stopped, AgentStatusReason.KillSwitch);

        app.Book.CancelOutside(placed.Stop);
        await app.ReconcileAsync();

        Assert.Equal([$"Closed {Q(placed.Quantity)} 59800 60400 KillSwitch"], await app.PartsAsync(agent.Id));
        Assert.Equal(0m, app.Book.FuturesAmount);
    }

    /// <summary>
    /// Hazard: the cycle checks the status when it starts, then waits for its AI. An entry it sizes after the agent was
    /// stopped is refused under the agent's lock, so it can't open a position the kill switch has just closed.
    /// </summary>
    [Theory]
    [InlineData(AgentStatus.Stopped)]
    [InlineData(AgentStatus.Paused)]
    public async Task Nothing_is_sent_for_an_agent_stopped_or_paused_while_its_cycle_ran(AgentStatus status)
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.SetStatusAsync(agent.Id, status);

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal((ExecutionStatus.NotSent, (ExecutionReason?)ExecutionReason.AgentNotActive), (result.Status, result.Reason));
        Assert.Empty(app.Book.Placed);
    }

    /// <summary>UC-05 end to end: one code, then each agent stopped and flat on Binance, with a result of its own.</summary>
    [Fact]
    public async Task Stop_all_agents_closes_every_agents_position_after_one_code()
    {
        await using var app = new ExecutionApp(postgres);
        using var visitor = Visitor.To(app.Agents.App);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(userId, await app.Agents.AddBinanceAsync(userId), await app.Agents.AddAiAsync(userId));
        var futures = await ActiveAsync(app, user, "Futures", Market.Futures);
        var spot = await ActiveAsync(app, user, "Spot", Market.Spot);
        await app.ExecuteAsync(app.Request(user, futures, ExecutionApp.Long(Market.Futures)));
        await app.ExecuteAsync(app.Request(user, spot, ExecutionApp.Long(Market.Spot)));
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.PauseAsync(user.Id, spot.Id, Cancellation)).Outcome);
        var key = await TwoFactorSteps.StartAsync(visitor);
        await TwoFactorSteps.EnableAsync(visitor, key, app.Clock);

        var result = await app.Agents.Agents.StopAllAsync(user.Id, app.Clock.NextCode(key), Cancellation);

        Assert.Equal(["Futures Closed", "Spot Closed"], result.Agents.Select(agent => $"{agent.Name} {agent.Result.Outcome}"));
        Assert.Equal(0m, app.Book.FuturesAmount);
        Assert.Equal(["Closed 0.01664 59800 60400 KillSwitch"], await app.PartsAsync(spot.Id));
        Assert.Equal(AgentStatus.Stopped, (await app.Agents.Agents.FindAsync(user.Id, futures.Id, Cancellation))!.Status);
        Assert.Equal(AgentStatus.Stopped, (await app.Agents.Agents.FindAsync(user.Id, spot.Id, Cancellation))!.Status);
        Assert.Equal(2, (await app.Agents.AuditAsync(user.Id)).Count(row => row.Action == "AgentKillSwitch"));
        Assert.Equal(2, (await app.NotificationsAsync(user.Id)).Count(type => type == "KillSwitchExecuted"));
    }

    private static async Task<AgentRecord> ActiveAsync(ExecutionApp app, AgentApp.User user, string name, Market market)
    {
        var agent = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, name, market) with { ExecutionMode = ExecutionMode.Autonomous });
        await app.SetStatusAsync(agent.Id, AgentStatus.Active);

        return (await app.Agents.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
    }

    private static Task<bool> HasOpenAsync(ExecutionApp app, Guid userId, Guid agentId) =>
        app.Agents.App.Services.GetRequiredService<IAgentExposure>().HasOpenPositionsOrOrdersAsync(userId, agentId, Cancellation);

    private static string Q(decimal value) => value.ToString("0.#############", CultureInfo.InvariantCulture);
}
