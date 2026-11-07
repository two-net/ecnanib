using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-UI-04, FR-RISK-10, FR-KEY-09 and NFR-SEC-04 through the contracts: a cycle's whole trace, its timeline and the
/// orders it placed, for its user only, with anything shaped like a secret masked.
/// </summary>
public sealed class TraceReadTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task A_trace_has_what_the_cycle_saw_each_call_and_the_risk_engines_rules()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var cycle = Assert.Single(await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation));

        var trace = await app.Traces.FindAsync(user.Id, agent.Id, cycle.CycleId, Cancellation);

        Assert.NotNull(trace);
        Assert.Equal(
            (CycleOutcome.AwaitingApproval, TradingEnvironment.Demo, Market.Spot, "BTCUSDT", Timeframe.OneHour, app.LastClose),
            (trace.Outcome, trace.Environment, trace.Market, trace.Symbol, trace.Timeframe, trace.CandleClose));
        Assert.NotNull(trace.FinishedAt);
        Assert.Null(trace.Budget);

        var snapshot = Assert.IsType<InputSnapshot>(trace.Snapshot);
        Assert.Equal("BTCUSDT", snapshot.Symbol);
        Assert.Equal(CycleApp.Ask, snapshot.Quote.AskPrice);
        Assert.NotNull(snapshot.LastCandle);
        Assert.Equal(
            [RiskRule.Settings, RiskRule.DailyLoss, RiskRule.WeeklyLoss, RiskRule.ConsecutiveLosses],
            trace.LossLimits.Select(rule => rule.Rule));

        Assert.Equal([AiRole.TechnicalAnalyst, AiRole.Strategist, AiRole.RiskCritic], trace.Roles.Select(role => role.Role));
        var strategist = trace.Roles.Single(role => role.Role == AiRole.Strategist);
        Assert.Equal(RoleState.Done, strategist.State);
        var call = Assert.Single(strategist.Calls);
        Assert.Equal((1, CouncilCallStatus.Answered, (CouncilFailure?)null), (call.Attempt, call.Status, call.Failure));
        Assert.False(string.IsNullOrWhiteSpace(call.Instructions));
        Assert.Contains("BTCUSDT", call.Message, StringComparison.Ordinal);
        Assert.Contains("Momentum above the 20 EMA.", call.Answer, StringComparison.Ordinal);
        Assert.Contains("\"action\": \"BUY\"", call.Output, StringComparison.Ordinal);
        Assert.Contains('\n', call.Output!);
        Assert.Empty(call.Violations);
        Assert.NotEmpty(call.Templates);
        Assert.Equal((1000L, 200L), (call.InputTokens, call.OutputTokens));

        // The test model has no price, so every token is counted apart from the cost, and the sum is the calls'.
        Assert.Equal(new TraceUsage(3000, 600, null, 3600, trace.Usage.AiTime), trace.Usage);
        Assert.Equal(trace.Usage, cycle.Usage);

        var evaluation = Assert.IsType<EvaluationTrace>(trace.Evaluation);
        Assert.Equal(RiskVerdict.Pass, evaluation.Verdict);
        Assert.Equal(Enum.GetValues<RiskRule>(), evaluation.Rules.Select(rule => rule.Rule));
        Assert.NotEmpty(evaluation.Rules.Single(rule => rule.Rule == RiskRule.StopDistance).Inputs);
        Assert.Equal(IntentAction.Buy, evaluation.Intent.Action);
        Assert.Equal((SizedOrderKind.Entry, 60118m), (evaluation.Sized!.Kind, evaluation.Sized.StopPrice));
        Assert.NotNull(evaluation.Sized.Quantity);

        var approval = Assert.IsType<ApprovalTrace>(trace.Approval);
        Assert.Equal(ApprovalStatus.Pending, approval.Record.Status);
        Assert.Empty(approval.RevalidationRules);
        Assert.Null(trace.Execution);
    }

    [Fact]
    public async Task An_approved_cycle_has_its_second_look_in_full_and_the_orders_it_placed()
    {
        await using var app = new CycleApp(postgres);
        _ = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var pending = await app.PendingAsync(user);
        await app.Approvals.ApproveAsync(user.Id, pending.Id, Cancellation);

        var trace = (await app.Traces.FindAsync(user.Id, agent.Id, pending.CycleId, Cancellation))!;
        var orders = await OrdersOf(app).ListAsync(user.Id, agent.Id, pending.CycleId, Cancellation);

        Assert.Equal((ApprovalStatus.Executed, true), (trace.Approval!.Record.Status, trace.Approval.Record.DecidedByUser));
        Assert.Equal(Enum.GetValues<RiskRule>(), trace.Approval.RevalidationRules.Select(rule => rule.Rule));
        Assert.DoesNotContain(trace.Approval.RevalidationRules, rule => rule.Outcome == RuleOutcome.Fail);
        Assert.Equal(ExecutionStatus.Protected, trace.Approval.Record.Execution!.Status);
        Assert.Null(trace.Execution);

        Assert.Equal([OrderLeg.Entry, OrderLeg.Stop, OrderLeg.Target], orders.Select(order => order.Leg).Order());
        var entry = orders[0];
        Assert.Equal(
            (OrderLeg.Entry, OrderKind.Market, OrderSide.Buy, OrderStatus.Filled, TradingEnvironment.Demo, "BTCUSDT"),
            (entry.Leg, entry.Kind, entry.Side, entry.Status, entry.Environment, entry.Symbol));
        Assert.Equal(entry.Quantity, entry.ExecutedQuantity);
        Assert.All(orders.Skip(1), order => Assert.Equal((OrderSide.Sell, OrderStatus.Open), (order.Side, order.Status)));

        Assert.Empty(await OrdersOf(app).ListAsync(Guid.NewGuid(), agent.Id, pending.CycleId, Cancellation));
        Assert.Empty(await OrdersOf(app).ListAsync(user.Id, Guid.NewGuid(), pending.CycleId, Cancellation));
    }

    [Fact]
    public async Task An_autonomous_cycle_has_what_execution_did_and_its_orders()
    {
        await using var app = new CycleApp(postgres);
        _ = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { ExecutionMode = ExecutionMode.Autonomous });
        await app.RunAsync(user, agent);
        var cycle = Assert.Single(await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation));

        var trace = (await app.Traces.FindAsync(user.Id, agent.Id, cycle.CycleId, Cancellation))!;
        var orders = await OrdersOf(app).ListAsync(user.Id, agent.Id, cycle.CycleId, Cancellation);

        Assert.Equal(CycleOutcome.Executed, trace.Outcome);
        Assert.Equal(ExecutionStatus.Protected, trace.Execution!.Status);
        Assert.Null(trace.Approval);
        Assert.Equal(3, orders.Count);
        Assert.Equal(OrderLeg.Entry, orders[0].Leg);
    }

    /// <summary>UC-02 A2's trace: the answer that broke the contract, with what broke it, then the retry that conformed.</summary>
    [Fact]
    public async Task A_retry_is_a_call_of_its_own_after_the_answer_that_broke_the_contract()
    {
        await using var app = new CycleApp(postgres);
        var answers = 0;
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(Interlocked.Increment(ref answers) == 1 ? """{"action":"BUY"}""" : CycleApp.Hold);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var cycle = Assert.Single(await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation));

        var trace = (await app.Traces.FindAsync(user.Id, agent.Id, cycle.CycleId, Cancellation))!;
        var calls = trace.Roles.Single(role => role.Role == AiRole.Strategist).Calls;

        Assert.Equal(CycleOutcome.Hold, trace.Outcome);
        Assert.Equal(TradeAction.Hold, cycle.Action);
        Assert.Equal([(1, CouncilCallStatus.BrokeContract), (2, CouncilCallStatus.Answered)], calls.Select(call => (call.Attempt, call.Status)));
        Assert.NotEmpty(calls[0].Violations);
        Assert.Null(calls[0].Output);
        Assert.Empty(calls[1].Violations);
        Assert.NotNull(calls[1].Output);
        Assert.Null(trace.Evaluation);
        Assert.Null(trace.Approval);
    }

    /// <summary>FR-CYC-07: the check that put the cycle in protective-only mode, and no snapshot or call it never made.</summary>
    [Fact]
    public async Task An_over_budget_cycle_has_its_budget_check_and_nothing_it_didnt_do()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { Budget = new AiBudget(null, null, 1, null) });
        await app.RunAsync(user, agent);
        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.RunAsync(user, agent);
        var cycles = await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation);

        var trace = (await app.Traces.FindAsync(user.Id, agent.Id, cycles[0].CycleId, Cancellation))!;

        Assert.Equal([CycleOutcome.OverBudget, CycleOutcome.AwaitingApproval], cycles.Select(cycle => cycle.Outcome));
        Assert.Equal(BudgetLimit.DailyTokens, trace.Budget!.Reached);
        Assert.Equal(3600L, trace.Budget.Spend.DayTokens);
        Assert.NotNull(trace.Budget.Until);
        Assert.Null(trace.Snapshot);
        Assert.All(trace.Roles, role => Assert.Equal((RoleState.Skipped, 0), (role.State, role.Calls.Count)));
        Assert.Equal(TraceUsage.None, trace.Usage);
        Assert.Equal(TraceUsage.None, cycles[0].Usage);
        Assert.Equal(3600L, cycles[1].Usage.UnpricedTokens);
    }

    [Fact]
    public async Task The_timeline_is_newest_first_in_each_cycles_own_environment_and_only_the_users_own()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        var first = app.LastClose;
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Hold);

        await app.RunAsync(user, agent);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            app.Clock.Advance(TimeSpan.FromHours(1));
            await app.RunAsync(user, agent);
        }

        // As after a switch to Live: the agent keeps its Demo cycles, and a Live one is labelled with its own.
        await Sql.ExecuteAsync(
            app.ConnectionString,
            $"UPDATE decision_cycles SET environment = 'Live' WHERE agent_id = '{agent.Id}' AND candle_close = '{first.AddHours(2):O}'",
            Cancellation);

        var all = await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation);
        var newest = await app.Traces.ListRecentAsync(user.Id, agent.Id, 2, Cancellation);

        Assert.Equal(
            [(first.AddHours(2), TradingEnvironment.Live), (first.AddHours(1), TradingEnvironment.Demo), (first, TradingEnvironment.Demo)],
            all.Select(cycle => (cycle.CandleClose, cycle.Environment)));
        Assert.All(all, cycle => Assert.Equal((CycleOutcome.Hold, (TradeAction?)TradeAction.Hold), (cycle.Outcome, cycle.Action)));
        Assert.Equal(all.Take(2).Select(cycle => cycle.CycleId), newest.Select(cycle => cycle.CycleId));
        Assert.Empty(await app.Traces.ListRecentAsync(user.Id, agent.Id, 0, Cancellation));
        Assert.Equal(3, (await app.Traces.ListRecentAsync(user.Id, agent.Id, int.MaxValue, Cancellation)).Count);
        Assert.Equal(TradingEnvironment.Live, (await app.Traces.FindAsync(user.Id, agent.Id, all[0].CycleId, Cancellation))!.Environment);

        Assert.Empty(await app.Traces.ListRecentAsync(Guid.NewGuid(), agent.Id, 20, Cancellation));
        Assert.Null(await app.Traces.FindAsync(Guid.NewGuid(), agent.Id, all[0].CycleId, Cancellation));
        Assert.Null(await app.Traces.FindAsync(user.Id, Guid.NewGuid(), all[0].CycleId, Cancellation));
        Assert.Null(await app.Traces.FindAsync(user.Id, agent.Id, Guid.NewGuid(), Cancellation));
    }

    /// <summary>
    /// FR-KEY-09's backstop: a model that echoes a key, or something shaped like one, has it masked in every text the trace
    /// gives, including the next role's prompt, which quotes it, and the approval.
    /// </summary>
    [Fact]
    public async Task Anything_shaped_like_a_secret_is_masked()
    {
        await using var app = new CycleApp(postgres);
        var rationale = $"Momentum, says {CredentialApp.AiKey} and {ExchangeHarness.ExampleApiKey}.";
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Buy.Replace("Momentum above the 20 EMA.", rationale, StringComparison.Ordinal));
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        var cycle = Assert.Single(await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation));

        var trace = (await app.Traces.FindAsync(user.Id, agent.Id, cycle.CycleId, Cancellation))!;
        var json = JsonSerializer.Serialize(trace);
        var answer = trace.Roles.Single(role => role.Role == AiRole.Strategist).Calls.Single().Answer;
        var critic = trace.Roles.Single(role => role.Role == AiRole.RiskCritic).Calls.Single().Message;

        Assert.NotNull(trace.Approval);
        Assert.DoesNotContain(CredentialApp.AiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ExchangeHarness.ExampleApiKey, json, StringComparison.Ordinal);
        Assert.Contains("Momentum, says ***", answer, StringComparison.Ordinal);
        Assert.Contains("Momentum, says ***", critic, StringComparison.Ordinal);
        Assert.StartsWith("Momentum, says ***", trace.Approval.Record.Proposal.StrategistRationale, StringComparison.Ordinal);
    }

    private static ICycleOrders OrdersOf(CycleApp app) => app.Agents.App.Services.GetRequiredService<ICycleOrders>();
}
