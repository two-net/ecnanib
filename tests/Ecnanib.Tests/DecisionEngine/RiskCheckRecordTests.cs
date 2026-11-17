using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Persistence;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Each Risk Guard check stores the outcome of every rule, so a user can see exactly why a plan
/// was accepted or rejected (FR-RSK-9). A check is stored with its cycle, in <c>risk_checks</c>
/// and <c>risk_check_rules</c>, and is its user's. See "The record of a check" in
/// docs/risk-guard.md.
/// </summary>
public class RiskCheckRecordTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Close = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The team of the agent that is called through the gateway: each role at a provider of its own.</summary>
    private static readonly IReadOnlyDictionary<Role, string> Team = new Dictionary<Role, string>
    {
        [Role.MarketAnalyst] = "gpt",
        [Role.SentimentAnalyst] = "grok",
        [Role.Strategist] = "gemini",
        [Role.RiskManager] = "claude",
    };

    [Fact]
    public async Task A_check_is_stored_with_the_outcome_of_every_rule_and_read_as_it_was_made()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // A plan that the Risk Guard rejected: its reward is 1.4 times its risk, and its order
        // has no size.
        var cycle = Cycle(agent, ada.Id, Close);
        var rejected = GuardCases.OpenLongOnFutures with { TakeProfit = 61400m, Size = null };
        var made = GuardCheck.Of(rejected);

        await application.CycleRecords.StoreAsync(
            cycle,
            [],
            [GuardedCycles.Check(cycle.Id, ada.Id, Close.AddSeconds(39), rejected, PlanAction.OpenLong)],
            TestContext.Current.CancellationToken);

        // One row for the check, with what the plan does and the verdict by their names.
        Assert.Equal(
            ["1|OpenLong|Rejected"],
            application.Database.Column("SELECT sequence || '|' || action || '|' || verdict FROM risk_checks"));

        // And one for each rule, by its name, also for a rule that the plan is not held to.
        Assert.Equal(
            Enum.GetNames<GuardRule>().Order(StringComparer.Ordinal),
            application.Database.Column("SELECT rule FROM risk_check_rules ORDER BY rule COLLATE \"C\""));
        Assert.Equal(
            ["Failed", "NotApplied", "Passed", "Unknown"],
            application.Database.Column("SELECT DISTINCT outcome FROM risk_check_rules ORDER BY outcome"));

        // Its user reads it as it was made: the close and the mode of its cycle, and every
        // rule in the order of the rules, with what it measured and what it held that to.
        var history = await application.RiskCheckRecords.LatestAsync(ada.Id, agent, TestContext.Current.CancellationToken);
        var read = Assert.Single(history.Checks);

        Assert.Equal(1, history.Total);
        Assert.Equal(
            (Close, TradingMode.Demo, 1, PlanAction.OpenLong, Close.AddSeconds(39), GuardVerdict.Rejected),
            (read.ClosedAt, read.Mode, read.Sequence, read.Action, read.CheckedAt, read.Verdict));
        Assert.Equal(made.Rules, read.Rules);
        Assert.Equal(
            (RuleOutcome.Failed, 1.4m, 1.5m),
            (read.Rules[(int)GuardRule.RewardToRisk].Outcome, read.Rules[(int)GuardRule.RewardToRisk].Measured, read.Rules[(int)GuardRule.RewardToRisk].Threshold));
    }

    [Fact]
    public async Task A_cycle_that_runs_stores_what_the_Risk_Guard_made_of_its_plan()
    {
        // The providers answer each role as its schema asks: a plan to open long, which the
        // Risk Manager approves.
        var providers = new StandInSystem(async (request, cancellation) =>
        {
            var provider = ModelCalls.ProviderOf(request);
            var role = Team.Single(given => given.Value == provider).Key;
            var offered = (await request.Content!.ReadAsStringAsync(cancellation)).Contains("\"tools\"", StringComparison.Ordinal);
            var answered = TeamAnswers.Of(role);

            return StandInSystem.Json(HttpStatusCode.OK, offered ? ModelCalls.Searched(provider, answered) : ModelCalls.Finished(provider, answered));
        });

        using var application = new CyclesApplication(Close, providers: providers);
        var (ada, agent) = await RunningAsync(application, Ada);

        await application.RunAsync(Close).Within();

        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var cycle = await context.Set<DecisionCycle>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var check = await context.Set<RiskCheck>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        // The team decided, and the Risk Guard checked what went on: both are in the record
        // of the cycle, which was stored once.
        Assert.Equal(CycleOutcome.Approved, cycle.Outcome);
        Assert.Equal(
            (cycle.Id, ada, 1, PlanAction.OpenLong, GuardVerdict.Rejected),
            (check.CycleId, check.UserId, check.Sequence, check.Action, check.Verdict));
        Assert.InRange(check.CheckedAt, cycle.StartedAt, cycle.EndedAt);

        // A rule is stored by its name, so they are put in the order of the rules here.
        var rules = (await context.Set<RiskCheckRule>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
            .OrderBy(rule => rule.Rule)
            .ToList();

        Assert.Equal(Enum.GetValues<GuardRule>(), rules.Select(rule => rule.Rule));
        Assert.All(rules, rule => Assert.Equal((check.Id, ada), (rule.CheckId, rule.UserId)));

        // The user has stored no Binance key, which Credentials says: neither the mode nor the
        // market of the agent is permitted. And nothing computes the size of the order.
        Assert.Equal(
            [GuardRule.ModePermitted, GuardRule.MarketPermitted],
            rules.Where(rule => rule.Outcome == RuleOutcome.Failed).Select(rule => rule.Rule));
        Assert.Equal(
            [GuardRule.QuantityStep, GuardRule.MinimumValue],
            rules.Where(rule => rule.Outcome == RuleOutcome.Unknown).Select(rule => rule.Rule));

        // And the user reads it.
        var read = Assert.Single(
            (await application.Application.RiskCheckRecords.LatestAsync(ada, agent, TestContext.Current.CancellationToken)).Checks);

        Assert.Equal((Close, GuardVerdict.Rejected), (read.ClosedAt, read.Verdict));
    }

    [Fact]
    public async Task A_valid_key_that_may_trade_on_the_market_permits_the_mode_and_the_market_of_an_agent()
    {
        var providers = new StandInSystem(async (request, cancellation) =>
        {
            var provider = ModelCalls.ProviderOf(request);
            var role = Team.Single(given => given.Value == provider).Key;
            var offered = (await request.Content!.ReadAsStringAsync(cancellation)).Contains("\"tools\"", StringComparison.Ordinal);
            var answered = TeamAnswers.Of(role);

            return StandInSystem.Json(HttpStatusCode.OK, offered ? ModelCalls.Searched(provider, answered) : ModelCalls.Finished(provider, answered));
        });

        using var application = new CyclesApplication(Close, providers: providers);
        var user = await application.Application.RegisterVerifiedAsync(Ada);

        // A valid Binance key for Demo, which may trade on both markets, stored before the agent.
        await application.Application.GiveKeysAsync(user.Id, Close.AddHours(-2));

        var agent = await RunningAgentAsync(application, user.Id);

        await application.RunAsync(Close).Within();

        var check = Assert.Single(
            (await application.Application.RiskCheckRecords.LatestAsync(user.Id, agent, TestContext.Current.CancellationToken)).Checks);

        // Only the size of the order stands in the way.
        Assert.Equal(RuleOutcome.Passed, check.Rules[(int)GuardRule.ModePermitted].Outcome);
        Assert.Equal(RuleOutcome.Passed, check.Rules[(int)GuardRule.MarketPermitted].Outcome);
        Assert.DoesNotContain(check.Rules, rule => rule.Outcome == RuleOutcome.Failed);
    }

    [Fact]
    public async Task A_cycle_and_its_checks_are_stored_together_or_not_at_all()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);
        var cycle = Cycle(agent, ada.Id, Close);

        // Two checks of a cycle with the number of the first: the database refuses the second,
        // and nothing of the cycle that it came with is stored.
        RiskCheckRecord[] twice =
        [
            GuardedCycles.Check(cycle.Id, ada.Id, Close.AddSeconds(39)),
            GuardedCycles.Check(cycle.Id, ada.Id, Close.AddSeconds(39)),
        ];

        await Assert.ThrowsAsync<DbUpdateException>(
            () => application.CycleRecords.StoreAsync(cycle, [], twice, TestContext.Current.CancellationToken));

        Assert.Equal(
            (0L, 0L, 0L),
            (
                application.Database.Scalar<long>("SELECT count(*) FROM decision_cycles"),
                application.Database.Scalar<long>("SELECT count(*) FROM risk_checks"),
                application.Database.Scalar<long>("SELECT count(*) FROM risk_check_rules")));

        // The two checks of a reverse that was rejected: the reverse, and its close.
        RiskCheckRecord[] ofAReverse =
        [
            GuardedCycles.Check(cycle.Id, ada.Id, Close.AddSeconds(39), GuardCases.Reverse with { Size = null }, PlanAction.Reverse),
            GuardedCycles.Check(cycle.Id, ada.Id, Close.AddSeconds(39), GuardCases.Close, PlanAction.Close, sequence: 2),
        ];

        await application.CycleRecords.StoreAsync(cycle, [], ofAReverse, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["1|Reverse|Rejected", "2|Close|Accepted"],
            application.Database.Column("SELECT sequence || '|' || action || '|' || verdict FROM risk_checks ORDER BY sequence"));
        Assert.Equal(2L * Enum.GetValues<GuardRule>().Length, application.Database.Scalar<long>("SELECT count(*) FROM risk_check_rules"));
    }

    [Fact]
    [ProvesOwnershipOf("risk_checks", "risk_check_rules")]
    public async Task The_checks_of_a_cycle_are_of_the_user_of_its_agent_and_of_no_other()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Close);
        var graces = await application.GiveAgentAsync(grace.Id, Close);

        var gracesCycle = await application.GiveCycleAsync(grace.Id, graces, Close);
        var before = application.RowsOf(grace.Id);

        // A check of Ada's agent is stored as Ada's, and changes nothing of Grace's.
        var adasCycle = await application.GiveCycleAsync(ada.Id, adas, Close);

        Assert.Equal(before, application.RowsOf(grace.Id));

        // Whoever asks for the checks of an agent finds those of the user who is signed in. A
        // user who names the agent of another finds none, as for an agent that does not exist.
        var checks = application.RiskCheckRecords;

        Assert.Equal(1, (await checks.LatestAsync(ada.Id, adas, TestContext.Current.CancellationToken)).Total);
        Assert.Equal(1, (await checks.LatestAsync(grace.Id, graces, TestContext.Current.CancellationToken)).Total);

        var notHers = await checks.LatestAsync(ada.Id, graces, TestContext.Current.CancellationToken);

        Assert.Equal((0, 0), (notHers.Total, notHers.Checks.Count));
        Assert.Equal(0, (await checks.LatestAsync(grace.Id, adas, TestContext.Current.CancellationToken)).Total);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [adasCycle],
            await context.Set<RiskCheck>().OwnedBy(ada.Id).Select(check => check.CycleId).ToListAsync(TestContext.Current.CancellationToken));

        var gracesCheck = await context.Set<RiskCheck>().OwnedBy(grace.Id).SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(gracesCycle, gracesCheck.CycleId);
        Assert.Equal(
            [gracesCheck.Id],
            await context.Set<RiskCheckRule>().OwnedBy(grace.Id).Select(rule => rule.CheckId).Distinct().ToListAsync(TestContext.Current.CancellationToken));

        // The owner of a check is the owner of its cycle, and the owner of an outcome the owner
        // of its check: the database holds to both, whatever wrote the row.
        var gracesNext = Cycle(graces, grace.Id, Close.AddHours(1));
        var asAda = GuardedCycles.Check(gracesNext.Id, ada.Id, Close.AddHours(1));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => application.CycleRecords.StoreAsync(gracesNext, [], [asAda], TestContext.Current.CancellationToken));

        var asGrace = GuardedCycles.Check(gracesNext.Id, grace.Id, Close.AddHours(1));
        var withAdasRules = asGrace with
        {
            Rules = [.. asGrace.Rules.Select(rule => new RiskCheckRule(rule.CheckId, ada.Id, rule.Rule, rule.Outcome, rule.Measured, rule.Threshold))],
        };

        await Assert.ThrowsAsync<DbUpdateException>(
            () => application.CycleRecords.StoreAsync(gracesNext, [], [withAdasRules], TestContext.Current.CancellationToken));

        Assert.Equal(before, application.RowsOf(grace.Id));
        Assert.Single(application.RowsOf(ada.Id)["risk_checks"]);
        Assert.Equal(Enum.GetValues<GuardRule>().Length, application.RowsOf(ada.Id)["risk_check_rules"].Count);
    }

    [Fact]
    public async Task The_checks_of_a_cycle_go_with_its_agent()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var kept = await application.GiveAgentAsync(ada.Id, Close, name: "Kept");
        var deleted = await application.GiveAgentAsync(ada.Id, Close, name: "Deleted", symbol: "SOLUSDT");
        var keptCycle = await application.GiveCycleAsync(ada.Id, kept, Close);
        await application.GiveCycleAsync(ada.Id, deleted, Close);

        Assert.Equal(2L, application.Database.Scalar<long>("SELECT count(*) FROM risk_checks"));

        // The agent is deleted, as its user deletes one: its cycles go with it, and with them
        // their checks and the outcomes of their rules.
        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Agent>().Where(agent => agent.Id == deleted).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal([keptCycle.ToString()], application.Database.Column("SELECT cycle_id::text FROM risk_checks"));
        Assert.Equal((long)Enum.GetValues<GuardRule>().Length, application.Database.Scalar<long>("SELECT count(*) FROM risk_check_rules"));
        Assert.DoesNotContain(deleted.ToString(), application.EverythingStored(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_latest_checks_of_an_agent_are_read_latest_close_first_each_in_the_mode_of_its_cycle()
    {
        using var application = new AccountsApplication(clock: new TestClock(Close));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // 22 cycles, an hour apart, each with one check. The agent was in Live for the first.
        for (var hour = 0; hour < 22; hour++)
        {
            var cycle = Cycle(agent, ada.Id, Close.AddHours(hour), hour == 0 ? TradingMode.Live : TradingMode.Demo);

            await application.CycleRecords.StoreAsync(
                cycle, [], [GuardedCycles.Check(cycle.Id, ada.Id, cycle.ClosedAt.AddSeconds(39))], TestContext.Current.CancellationToken);
        }

        // And the latest, with the two checks of a reverse that was rejected.
        var latest = Cycle(agent, ada.Id, Close.AddHours(22));

        await application.CycleRecords.StoreAsync(
            latest,
            [],
            [
                GuardedCycles.Check(latest.Id, ada.Id, latest.ClosedAt.AddSeconds(39), GuardCases.Reverse with { Size = null }, PlanAction.Reverse),
                GuardedCycles.Check(latest.Id, ada.Id, latest.ClosedAt.AddSeconds(39), GuardCases.Close, PlanAction.Close, sequence: 2),
            ],
            TestContext.Current.CancellationToken);

        var history = await application.RiskCheckRecords.LatestAsync(ada.Id, agent, TestContext.Current.CancellationToken);

        // A page shows the latest 20 of the 24, and says how many there are.
        Assert.Equal((24, 20), (history.Total, history.Checks.Count));

        // The latest close first, and the checks of one cycle in their order.
        Assert.Equal(
            [(Close.AddHours(22), 1, PlanAction.Reverse), (Close.AddHours(22), 2, PlanAction.Close), (Close.AddHours(21), 1, PlanAction.OpenLong)],
            history.Checks.Take(3).Select(check => (check.ClosedAt, check.Sequence, check.Action)));
        Assert.Equal(
            Enumerable.Range(0, 18).Select(back => Close.AddHours(21 - back)),
            history.Checks.Skip(2).Select(check => check.ClosedAt));
        Assert.All(history.Checks, check => Assert.Equal(Enum.GetValues<GuardRule>(), check.Rules.Select(rule => rule.Rule)));

        // What an agent did in Demo stays Demo, and what it did in Live stays Live (FR-BIN-11):
        // a check says the mode of its cycle, and not the mode that the agent is in now.
        Assert.All(history.Checks, check => Assert.Equal(TradingMode.Demo, check.Mode));
        Assert.Equal(
            ["Live"],
            application.Database.Column(
                "SELECT c.mode FROM risk_checks r JOIN decision_cycles c ON c.id = r.cycle_id ORDER BY c.closed_at LIMIT 1"));
    }

    /// <summary>A cycle of an agent that ended with a plan that the Risk Manager approved.</summary>
    private static DecisionCycle Cycle(Guid agentId, Guid userId, DateTimeOffset closedAt, TradingMode mode = TradingMode.Demo) =>
        new(Guid.CreateVersion7(), agentId, userId, mode, closedAt, closedAt.AddSeconds(2), closedAt.AddSeconds(40), CycleOutcome.Approved);

    /// <summary>
    /// Registers a user and gives them an agent that runs on 15 minutes, with a team of four
    /// providers and a stored key for each, as the application stores them.
    /// </summary>
    private static async Task<(Guid UserId, Guid AgentId)> RunningAsync(CyclesApplication application, string email)
    {
        var user = await application.Application.RegisterVerifiedAsync(email);

        return (user.Id, await RunningAgentAsync(application, user.Id));
    }

    private static async Task<Guid> RunningAgentAsync(CyclesApplication application, Guid userId)
    {
        var agent = await application.Application.GiveAgentAsync(
            userId,
            Close.AddHours(-1),
            state: AgentState.Running,
            cause: AgentStateCause.UserStarted,
            team: false,
            timeframe: Timeframe.FifteenMinutes);

        foreach (var (role, provider) in Team)
        {
            await application.Application.GiveRoleAsync(userId, agent, role, Close.AddHours(-1), provider, $"{provider}-test-model");
        }

        return agent;
    }
}
