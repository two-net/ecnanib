using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// UC-02's main flow (FR-CYC-02): the analysts, the Strategist, the Risk Critic and the Risk Engine, with the whole
/// trace saved (FR-UI-04), each role's state published (FR-ROLE-08), and a sized order at the end.
/// </summary>
public sealed class DecisionCycleTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_buy_the_critic_approves_ends_with_a_sized_order_and_a_full_trace()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["ReadyToExecute"], await app.CyclesAsync(agent.Id));
        Assert.Equal(
            ["TechnicalAnalyst Test Answered", "Strategist Test Answered", "RiskCritic Test Answered"],
            await app.CallsAsync(agent.Id));
        Assert.Equal(["TechnicalAnalyst Done", "Strategist Done", "RiskCritic Done"], await app.RolesAsync(user.Id, agent.Id));

        // The intent takes the nearer target, which gives the lowest reward-to-risk (FR-RISK-01).
        var evaluation = (await app.EvaluationAsync(agent.Id))!.Value;
        Assert.Equal("Pass", evaluation.GetProperty("verdict").GetString());
        Assert.Equal("Buy", evaluation.GetProperty("intent").GetProperty("action").GetString());
        Assert.Equal(60135m, evaluation.GetProperty("intent").GetProperty("targetPrice").GetDecimal());
        Assert.Equal(60118m, evaluation.GetProperty("intent").GetProperty("stopPrice").GetDecimal());
        Assert.True(evaluation.GetProperty("sizedOrder").GetProperty("quantity").GetDecimal() > 0m);
    }

    /// <summary>FR-ROLE-08: every role goes from queued to running to done, and the outcome comes last.</summary>
    [Fact]
    public async Task Each_change_is_published_as_it_happens()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        var changes = app.Observer.Changes.Where(change => change.AgentId == agent.Id).ToList();
        Assert.All(changes.First().Roles, role => Assert.Equal(RoleState.Queued, role.State));
        Assert.Null(changes.First().Outcome);

        foreach (var role in new[] { AiRole.TechnicalAnalyst, AiRole.Strategist, AiRole.RiskCritic })
        {
            var states = changes.Select(change => change.Roles.Single(entry => entry.Role == role).State).Distinct().ToList();
            Assert.Equal([RoleState.Queued, RoleState.Running, RoleState.Done], states);
        }

        var last = changes.Last();
        Assert.Equal(CycleOutcome.ReadyToExecute, last.Outcome);
        Assert.Equal("BUY: Momentum above the 20 EMA.", last.Roles.Single(role => role.Role == AiRole.Strategist).Summary);
        Assert.Equal(last, await app.Traces.FindLatestAsync(user.Id, agent.Id, CycleApp.Cancellation), CycleProgressComparer.Instance);
    }

    /// <summary>
    /// NFR-SEC-08: the analysts' output reaches the Strategist as untrusted content, validated, not as their raw
    /// answers. The trace keeps the prompt, the answer and the template versions (FR-PRM-03, FR-UI-04), and its
    /// tokens add up on the cycle.
    /// </summary>
    [Fact]
    public async Task The_trace_keeps_each_prompt_and_answer_with_earlier_roles_as_untrusted()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        var message = Assert.Single(await app.CallColumnAsync(agent.Id, AiRole.Strategist, "message"));
        Assert.Contains("kind=\"role_output\"", message, StringComparison.Ordinal);
        Assert.Contains("Uptrend holding above the 20 EMA.", message, StringComparison.Ordinal);
        Assert.Contains("<runtime_context>", message, StringComparison.Ordinal);

        var critic = Assert.Single(await app.CallColumnAsync(agent.Id, AiRole.RiskCritic, "message"));
        Assert.Contains("Momentum above the 20 EMA.", critic, StringComparison.Ordinal);

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(await app.CallColumnAsync(agent.Id, AiRole.Strategist, "instructions"))));
        Assert.Contains("\"action\": \"BUY\"", Assert.Single(await app.CallColumnAsync(agent.Id, AiRole.Strategist, "output")), StringComparison.Ordinal);
        Assert.NotEqual("[]", Assert.Single(await app.CallColumnAsync(agent.Id, AiRole.Strategist, "templates")));

        Assert.Equal(
            3000L,
            await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT input_tokens FROM decision_cycles WHERE agent_id = '{agent.Id}'", CycleApp.Cancellation));
    }

    /// <summary>FR-KEY-09: no key reaches the trace, whatever a column holds.</summary>
    [Fact]
    public async Task No_key_is_in_the_trace()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        var trace = string.Join(
            '\n',
            await Sql.ColumnAsync(
                app.ConnectionString,
                """
                SELECT d::text FROM decision_cycles d
                UNION ALL SELECT c::text FROM ai_calls c
                UNION ALL SELECT r::text FROM risk_evaluations r
                UNION ALL SELECT o::text FROM cycle_roles o
                """,
                CycleApp.Cancellation));

        Assert.DoesNotContain(CredentialApp.AiKey, trace, StringComparison.Ordinal);
        Assert.DoesNotContain(Exchange.ExchangeHarness.ExampleApiKey, trace, StringComparison.Ordinal);
        Assert.DoesNotContain(Exchange.ExchangeHarness.ExampleSecret, trace, StringComparison.Ordinal);
    }

    /// <summary>A HOLD needs no critic and no Risk Engine, and the critic is skipped.</summary>
    [Fact]
    public async Task A_hold_ends_the_cycle_without_the_critic()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Hold);
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["Hold"], await app.CyclesAsync(agent.Id));
        Assert.Equal(["TechnicalAnalyst Done", "Strategist Done", "RiskCritic Skipped"], await app.RolesAsync(user.Id, agent.Id));
        Assert.Null(await app.EvaluationAsync(agent.Id));
    }

    [Fact]
    public async Task A_rejection_ends_the_cycle_before_the_risk_engine()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.RiskCritic] = _ => CycleApp.Reply(CycleApp.Approve
            .Replace("\"verdict\":\"approve\"", "\"verdict\":\"reject\"", StringComparison.Ordinal));
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["RejectedByCritic"], await app.CyclesAsync(agent.Id));
        Assert.Null(await app.EvaluationAsync(agent.Id));
    }

    /// <summary>The critic's changes reduce risk, and the intent carries them.</summary>
    [Fact]
    public async Task A_modification_is_applied_to_the_intent()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.RiskCritic] = _ => CycleApp.Reply(CycleApp.Approve.Replace(
            "\"verdict\":\"approve\",\"modifications\":null",
            "\"verdict\":\"modify\",\"modifications\":{\"stop_price\":60119.5,\"target_prices\":[],\"risk_percent\":0.5}",
            StringComparison.Ordinal));
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["ReadyToExecute"], await app.CyclesAsync(agent.Id));
        var intent = (await app.EvaluationAsync(agent.Id))!.Value.GetProperty("intent");
        Assert.Equal(60119.5m, intent.GetProperty("stopPrice").GetDecimal());
        Assert.Equal(0.5m, intent.GetProperty("riskPercent").GetDecimal());
        Assert.Equal(60135m, intent.GetProperty("targetPrice").GetDecimal());
    }

    /// <summary>UC-02 A3: the engine fails the intent, nothing is sized, and the trace says which rule and why.</summary>
    [Fact]
    public async Task An_intent_the_risk_engine_fails_is_blocked_with_its_reasons()
    {
        await using var app = new CycleApp(postgres);
        app.Replies[AiRole.Strategist] = _ => CycleApp.Reply(CycleApp.Buy.Replace("\"stop_price\":60118", "\"stop_price\":60130", StringComparison.Ordinal));
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);

        Assert.Equal(["BlockedByRiskEngine"], await app.CyclesAsync(agent.Id));
        var evaluation = (await app.EvaluationAsync(agent.Id))!.Value;
        Assert.Equal("Fail", evaluation.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, evaluation.GetProperty("sizedOrder").ValueKind);
        Assert.Contains(
            evaluation.GetProperty("rules").EnumerateArray(),
            rule => rule.GetProperty("rule").GetString() == "StopSide" && rule.GetProperty("reason").GetString() == "StopWrongSide");
    }

    /// <summary>FR-CYC-06: an entry that passes after the deadline isn't one the cycle may make.</summary>
    [Fact]
    public async Task An_entry_after_the_deadline_is_not_made()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent, deadline: app.Clock.GetUtcNow() - TimeSpan.FromSeconds(1));

        Assert.Equal(["PastDeadline"], await app.CyclesAsync(agent.Id));
        Assert.Equal("Pass", (await app.EvaluationAsync(agent.Id))!.Value.GetProperty("verdict").GetString());
    }

    /// <summary>Compares progress by value, because its roles are a list.</summary>
    private sealed class CycleProgressComparer : IEqualityComparer<CycleProgress?>
    {
        public static readonly CycleProgressComparer Instance = new();

        public bool Equals(CycleProgress? x, CycleProgress? y) =>
            x is not null && y is not null
            && x with { Roles = [], StartedAt = default, FinishedAt = null } == y with { Roles = [], StartedAt = default, FinishedAt = null }
            && x.Roles.SequenceEqual(y.Roles);

        public int GetHashCode(CycleProgress? obj) => obj?.CycleId.GetHashCode() ?? 0;
    }
}
