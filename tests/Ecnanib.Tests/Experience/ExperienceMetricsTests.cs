using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.DecisionCycle;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// FR-EXP-01 and FR-EXP-02 end to end: real cycles' calls and decisions count for the models that made them, and a
/// trade counts for the models of the cycle that proposed its entry once it's settled.
/// </summary>
public sealed class ExperienceMetricsTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public async Task Each_roles_model_is_given_its_calls_and_decisions()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.Cycles.ActiveAgentAsync();

        await app.Cycles.RunAsync(user, agent);
        app.Clock.Advance(Hour);
        app.Cycles.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Hold);
        await app.Cycles.RunAsync(user, agent);
        await app.RefreshAsync();

        var report = await app.ReadAsync(user.Id);

        Assert.Equal([AiRole.TechnicalAnalyst, AiRole.Strategist, AiRole.RiskCritic], report.Models.Select(row => row.Role));

        var strategist = ExperienceApp.Row(report, AiRole.Strategist);
        Assert.Equal(2, strategist.Calls.Sent);
        Assert.Equal(2, strategist.Calls.Answered);
        Assert.Equal(2, strategist.Calls.AnsweredCycles);
        Assert.Equal(2 * ExperienceApp.CallCost, strategist.Calls.Cost);
        Assert.Equal(2_400, strategist.Calls.InputTokens + strategist.Calls.OutputTokens);
        Assert.Equal(0m, strategist.Calls.ErrorRate);
        Assert.NotNull(strategist.Calls.MedianLatency);
        Assert.Equal(new(Holds: 1, Entries: 1, Exits: 0, Adjustments: 0), strategist.Strategist);
        Assert.Null(strategist.Critic);

        // The Risk Critic isn't asked about a HOLD.
        var critic = ExperienceApp.Row(report, AiRole.RiskCritic);
        Assert.Equal(new(Approvals: 1, Modifications: 0, Rejections: 0), critic.Critic);
        Assert.Equal(ExperienceApp.CallCost, critic.Calls.Cost);

        // The trend was up both times, and three candles haven't closed since either.
        var technical = ExperienceApp.Row(report, AiRole.TechnicalAnalyst);
        Assert.Equal(new(BullishCalls: 2, BearishCalls: 0, Hits: 0, Misses: 0), technical.Analyst);
        Assert.Equal(2, technical.Analyst!.Unresolved);
        Assert.True(report.Provisional);
        Assert.Equal(0, report.UnattributedTrades);
    }

    [Fact]
    public async Task An_analysts_call_is_scored_once_its_horizon_has_closed()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.Cycles.ActiveAgentAsync();
        app.Cycles.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Hold);
        app.Cycles.Replies[AiRole.TechnicalAnalyst] = _ => CycleApp.Reply(Council.ContractSamples.Technical.Replace("\"trend\":\"up\"", "\"trend\":\"down\"", StringComparison.Ordinal));

        await app.Cycles.RunAsync(user, agent);
        app.Clock.Advance(Hour);
        app.Cycles.Replies.TryRemove(AiRole.TechnicalAnalyst, out _);
        await app.Cycles.RunAsync(user, agent);

        app.Clock.Advance(4 * Hour);
        await app.RefreshAsync();

        // The sample closes rose by 3 over each call's three candles, so up hit and down missed.
        var technical = ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.TechnicalAnalyst);
        Assert.Equal(new(BullishCalls: 1, BearishCalls: 1, Hits: 1, Misses: 1), technical.Analyst);
        Assert.Equal(0.5m, technical.Analyst!.HitRate);
    }

    [Fact]
    public async Task A_settled_trade_counts_for_the_models_of_the_cycle_that_proposed_its_entry()
    {
        await using var app = new ExperienceApp(postgres);
        var book = new FakeOrderBook(app.Cycles.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.Cycles.ActiveAgentAsync((_, settings) => settings with { ExecutionMode = ExecutionMode.Autonomous });

        await app.Cycles.RunAsync(user, agent);
        book.Trigger(book.Orders.Single(order => order.Type == "STOP_LOSS").ClientOrderId);

        // The next cycle's reconciliation books the stop-out, and its Strategist holds.
        app.Clock.Advance(Hour);
        app.Cycles.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Hold);
        await app.Cycles.RunAsync(user, agent);
        await app.RefreshAsync();

        // Closed but not settled, so it doesn't count yet.
        Assert.Equal(0, ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.Strategist).Trades.Trades);

        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE trades SET settled_at = closed_at WHERE agent_id = '{agent.Id}'", ExperienceApp.Cancellation);
        var pnl = await Sql.ScalarAsync<decimal>(app.ConnectionString, $"SELECT realized_pnl FROM trades WHERE agent_id = '{agent.Id}'", ExperienceApp.Cancellation);

        var report = await app.ReadAsync(user.Id);
        var strategist = ExperienceApp.Row(report, AiRole.Strategist);

        Assert.True(pnl < 0m);
        Assert.Equal(0, report.UnattributedTrades);
        Assert.Equal(1, strategist.Trades.Trades);
        Assert.Equal(1, strategist.Trades.Losses);
        Assert.Equal(0m, strategist.Trades.WinRate);
        Assert.Equal(pnl, Assert.Single(strategist.Trades.Results).TotalPnl);
        Assert.Equal("USDT", strategist.Trades.Results[0].QuoteAsset);

        // Two Strategist calls for one trade, the HOLD's included.
        Assert.Equal(2 * ExperienceApp.CallCost, strategist.Trades.AiCostPerTrade);
        Assert.Equal(pnl - (2 * ExperienceApp.CallCost), strategist.Trades.NetOfAiCostUsd);

        // The Technical Analyst and the Risk Critic that approved it took part too.
        Assert.Equal(1, ExperienceApp.Row(report, AiRole.TechnicalAnalyst).Trades.Trades);
        Assert.Equal(1, ExperienceApp.Row(report, AiRole.RiskCritic).Trades.Trades);
    }

    [Fact]
    public async Task A_rejection_by_the_user_is_not_the_Risk_Critics()
    {
        await using var app = new ExperienceApp(postgres);
        var (user, agent) = await app.Cycles.ActiveAgentAsync();

        await app.Cycles.RunAsync(user, agent);
        var pending = await app.Cycles.PendingAsync(user);
        await app.Cycles.Approvals.RejectAsync(user.Id, pending.Id, ExperienceApp.Cancellation);

        app.Clock.Advance(Hour);
        app.Cycles.Replies[AiRole.RiskCritic] = _ => CycleApp.Reply(CycleApp.Approve.Replace("\"approve\"", "\"reject\"", StringComparison.Ordinal));
        await app.Cycles.RunAsync(user, agent);
        await app.RefreshAsync();

        var critic = ExperienceApp.Row(await app.ReadAsync(user.Id), AiRole.RiskCritic);

        Assert.Equal(new(Approvals: 1, Modifications: 0, Rejections: 1), critic.Critic);
    }

    [Fact]
    public async Task A_refusal_counts_against_the_primary_and_the_cycle_for_the_fallback_that_answered()
    {
        await using var app = new ExperienceApp(postgres);
        app.Cycles.Replies[AiRole.Strategist] = request => request.Host == AiHarness.SecondHost
            ? CycleApp.Reply(CycleApp.Hold)
            : FakeAiProvider.Completion("{}", 1000, 200, stop: "refusal");
        var (user, agent) = await app.Cycles.ActiveAgentAsync(
            (user, settings) => settings with
            {
                Roles =
                [
                    .. settings.Roles.Where(role => role.Role != AiRole.Strategist),
                    new RoleAssignment(
                        AiRole.Strategist, new ModelRef(AgentApp.Provider, AgentApp.Model), new ModelRef(AgentApp.Second, AgentApp.Model)),
                ],
            },
            user => app.Cycles.Agents.AddAiAsync(user.Id, AgentApp.Second));

        await app.Cycles.RunAsync(user, agent);
        await app.RefreshAsync();

        var report = await app.ReadAsync(user.Id);
        var primary = ExperienceApp.Row(report, AiRole.Strategist);
        var fallback = ExperienceApp.Row(report, AiRole.Strategist, AgentApp.Second);

        Assert.Equal(1, primary.Calls.Refusals);
        Assert.Equal(1m, primary.Calls.RefusalRate);
        Assert.Equal(0, primary.Calls.AnsweredCycles);
        Assert.Equal(1, primary.Calls.Cycles);
        Assert.Equal(new(Holds: 0, Entries: 0, Exits: 0, Adjustments: 0), primary.Strategist);
        Assert.Equal(1, fallback.Calls.AnsweredCycles);
        Assert.Equal(1, fallback.Calls.FallbackCycles);
        Assert.Equal(new(Holds: 1, Entries: 0, Exits: 0, Adjustments: 0), fallback.Strategist);
    }
}
