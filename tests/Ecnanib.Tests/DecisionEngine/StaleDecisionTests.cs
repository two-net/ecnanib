using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// A decision that completes later than the stale limit for its timeframe cannot open a
/// position. It can still close a position or tighten protection (FR-CYC-9, SRS Appendix B).
/// See "The stale limit" in docs/team.md.
/// </summary>
public class StaleDecisionTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    /// <summary>A close of a candle of 15 minutes, and of no longer one.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    /// <summary>A close of a candle of 15 minutes and of one of an hour.</summary>
    private static readonly DateTimeOffset Hour = new(2026, 11, 18, 11, 0, 0, TimeSpan.Zero);

    /// <summary>The stale limit of 15m in the file of the tests.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(100);

    private IRoleResults Results => application.Services.GetRequiredService<IRoleResults>();

    [Fact]
    public void A_decision_is_stale_when_it_completes_later_than_the_stale_limit_of_its_timeframe()
    {
        var limits = application.Services.GetRequiredService<TradingSettings>().StaleSeconds;

        // Each timeframe has a limit of its own, which is the one of the configuration file.
        Assert.Equal(
            [100, 200, 400, 800],
            Timeframes.All.Select(timeframe => limits.Of(timeframe).TotalSeconds));

        Assert.All(Timeframes.All, timeframe =>
        {
            var limit = limits.Of(timeframe);

            // In time: at the close, within the limit, and at the limit itself.
            Assert.False(StaleLimit.IsStale(Hour, Hour, limit));
            Assert.False(StaleLimit.IsStale(Hour, Hour + (limit / 2), limit));
            Assert.False(StaleLimit.IsStale(Hour, Hour + limit, limit));

            // Stale: later than the limit, by the least and by much.
            Assert.True(StaleLimit.IsStale(Hour, Hour + limit + TimeSpan.FromTicks(1), limit));
            Assert.True(StaleLimit.IsStale(Hour, Hour + limit + TimeSpan.FromHours(1), limit));
        });

        // The limits of the specification, as the tracked file has them (SRS Appendix B).
        var tracked = new StaleSecondsSettings(FifteenMinutes: 120, OneHour: 300, FourHours: 600, OneDay: 900);

        Assert.Equal(
            [TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(900)],
            Timeframes.All.Select(tracked.Of));
    }

    [Fact]
    public void A_stale_plan_that_opens_long_or_short_does_not_go_on()
    {
        Assert.All(new[] { "open_long", "open_short" }, action =>
            Assert.Equal(
                new TeamDecision(CycleOutcome.Stale, GoesOn: null),
                StaleLimit.Of(Results.ReadPlan(TeamAnswers.Plan(action)).Value!)));
    }

    [Fact]
    public void Of_a_stale_reverse_the_close_goes_on_and_nothing_of_what_it_opens()
    {
        var reverse = Results.ReadPlan(TeamAnswers.Plan("reverse", "limit", entryPrice: 61250m)).Value!;
        var decision = StaleLimit.Of(reverse);

        Assert.Equal(CycleOutcome.Stale, decision.Outcome);

        // A plan to close, as the Strategist answers one: no entry, no protection, and no risk.
        var close = decision.GoesOn!;

        Assert.Equal(PlanAction.Close, close.Action);
        Assert.False(close.OpensPosition);
        Assert.Equal(
            (null, null, null, null, null),
            (close.EntryType, close.EntryPrice, close.StopLoss, close.TakeProfit, close.RiskFraction));
        Assert.Equal(reverse.Rationale, close.Rationale);

        // The plan that was read is as it was.
        Assert.Equal((PlanAction.Reverse, 61250m), (reverse.Action, reverse.EntryPrice));
    }

    [Fact]
    public void Only_a_plan_that_opens_a_position_is_held_to_the_stale_limit()
    {
        // A plan that holds, closes, or adjusts protection is never handed over: it goes on as
        // it is, in time or not.
        Assert.All(new[] { TeamAnswers.Hold, TeamAnswers.Close, TeamAnswers.Adjust }, answered =>
            Assert.Throws<ArgumentException>(() => StaleLimit.Of(Results.ReadPlan(answered).Value!)));
    }

    [Theory]
    [InlineData("open_long", Market.Spot)]
    [InlineData("open_long", Market.Futures)]
    [InlineData("open_short", Market.Futures)]
    public async Task A_decision_that_completes_later_than_the_stale_limit_cannot_open_a_position(string action, Market market)
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var models = cycles.Models;
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: market);

        models.Says(Role.Strategist, TeamAnswers.Plan(action));

        // The Strategist answers one second later than the limit after the close.
        await StrategistAnswersAsync(cycles, Quarter, Quarter + Limit + TimeSpan.FromSeconds(1));

        // No position is opened: the cycle ends as stale, and nothing goes on.
        var record = cycles.Records.Of(agent);

        Assert.Equal(CycleOutcome.Stale, record.Cycle.Outcome);

        // The plan cannot open whatever a review says, so no model is called to review it.
        Assert.Empty(models.CallsOf(Role.RiskManager));
        Assert.Equal(3, models.Calls.Count);
        Assert.Equal(RoleValidation.Valid, Assert.Single(record.Of(Role.Strategist)).Validation);

        // It is said once, as a warning, with how late the decision was and the limit of the timeframe.
        var stale = Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Stale));

        Assert.Equal(("Warning", agent.ToString()), (stale.Level, stale.Text("AgentId")));
        Assert.Equal(
            (101d, "15m", 100d, PlanOf(action), "nothing"),
            (stale["CompletedAfterSeconds"].GetDouble(), stale.Text("Timeframe"), stale["StaleSeconds"].GetDouble(), stale.Text("PlanAction"), stale.Text("GoesOn")));
        Assert.True(stale.Has("CycleId"));

        var decided = Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Decided));

        Assert.Equal(("Stale", PlanOf(action)), (decided.Text("CycleOutcome"), decided.Text("PlanAction")));

        // A stale decision is a decision: the cycle ended, later than its target, and did not fail.
        var summary = cycles.SummaryOf(Quarter);

        Assert.Equal((0, 1, 0), (summary["Ended"].GetInt32(), summary["EndedLate"].GetInt32(), summary["Failed"].GetInt32()));
    }

    [Fact]
    public async Task A_decision_that_completes_at_the_stale_limit_still_opens_a_position()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        // The Strategist answers at the limit itself, and the Risk Manager at once.
        await StrategistAnswersAsync(cycles, Quarter, Quarter + Limit);

        Assert.Equal(CycleOutcome.Approved, cycles.Records.Of(agent).Cycle.Outcome);
        Assert.Single(cycles.Models.CallsOf(Role.RiskManager));
        Assert.Empty(cycles.Written(TeamLog.Area, TeamLog.Stale));
    }

    [Fact]
    public async Task The_stale_limit_counts_from_the_close_of_the_candle_and_not_from_the_start_of_the_cycle()
    {
        // The turn of the cycle comes a minute after the close: it waited in the queue.
        using var cycles = new CyclesApplication(Quarter + TimeSpan.FromSeconds(60), buildsPackages: true);
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        // The Strategist answers 41 seconds into the cycle, which is 101 seconds after the close.
        await StrategistAnswersAsync(cycles, Quarter, Quarter + Limit + TimeSpan.FromSeconds(1));

        Assert.Equal(CycleOutcome.Stale, cycles.Records.Of(agent).Cycle.Outcome);
        Assert.Empty(cycles.Models.CallsOf(Role.RiskManager));
        Assert.Equal(101d, Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Stale))["CompletedAfterSeconds"].GetDouble());
    }

    public static TheoryData<string, string, CycleOutcome> PlansThatOpenNothing => new()
    {
        { "close", TeamAnswers.Close, CycleOutcome.Unreviewed },
        { "adjust protection", TeamAnswers.Adjust, CycleOutcome.Unreviewed },
        { "hold", TeamAnswers.Hold, CycleOutcome.Hold },
    };

    [Theory]
    [MemberData(nameof(PlansThatOpenNothing))]
    public async Task A_stale_decision_can_still_close_a_position_or_tighten_protection(string what, string plan, CycleOutcome outcome)
    {
        // The agent holds a long position, whose stop-loss the adjustment raises.
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: PlanAgents.Holding(PlanAgents.Long));
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        cycles.Models.Says(Role.Strategist, plan);

        // The Strategist answers long after the limit, close to when the cycles of the close are stopped.
        await StrategistAnswersAsync(cycles, Quarter, Quarter + TimeSpan.FromMinutes(10));

        // The plan goes on as it would in time: nothing stands in the way of reducing risk.
        var record = cycles.Records.Of(agent);

        Assert.True(outcome == record.Cycle.Outcome, $"A stale plan to {what} ended as {record.Cycle.Outcome}.");
        Assert.Empty(cycles.Written(TeamLog.Area, TeamLog.Stale));
        Assert.Equal(outcome.ToString(), Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Decided)).Text("CycleOutcome"));
    }

    [Fact]
    public async Task Of_a_stale_reverse_the_close_of_the_position_goes_on()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: PlanAgents.Holding(PlanAgents.Long));
        var models = cycles.Models;
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: Market.Futures);

        models.Says(Role.Strategist, TeamAnswers.Plan("reverse"));

        await StrategistAnswersAsync(cycles, Quarter, Quarter + Limit + TimeSpan.FromSeconds(30));

        // The opposite side is not opened, and the close is not reviewed: the Risk Manager
        // cannot block a plan that closes a position (FR-TEAM-7).
        Assert.Equal(CycleOutcome.Stale, cycles.Records.Of(agent).Cycle.Outcome);
        Assert.Empty(models.CallsOf(Role.RiskManager));

        var stale = Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Stale));

        Assert.Equal(("Reverse", "Close"), (stale.Text("PlanAction"), stale.Text("GoesOn")));
    }

    [Theory]

    // What the Risk Manager lets go on is stale by the time it is reviewed, and opens nothing.
    [InlineData("open_long", "approve", null, CycleOutcome.Stale, "nothing")]
    [InlineData("open_long", "reduce", 0.004, CycleOutcome.Stale, "nothing")]

    // Of a reverse that it lets go on, the close goes on.
    [InlineData("reverse", "approve", null, CycleOutcome.Stale, "Close")]
    [InlineData("reverse", "reduce", 0.004, CycleOutcome.Stale, "Close")]

    // A veto is a veto, in time or not: nothing goes on, and a reverse closes nothing.
    [InlineData("open_long", "veto", null, CycleOutcome.Vetoed, null)]
    [InlineData("reverse", "veto", null, CycleOutcome.Vetoed, null)]
    public async Task A_decision_completes_with_the_review_of_the_Risk_Manager(
        string action, string verdict, double? reduced, CycleOutcome outcome, string? goesOn)
    {
        // An agent on Futures, which holds a position where its plan reverses one.
        var plan = TeamAnswers.Plan(action);

        using var cycles = new CyclesApplication(Quarter, buildsPackages: true, holdings: PlanAgents.HoldingFor(plan));
        var models = cycles.Models;
        var agent = cycles.Agents!.Give(Guid.NewGuid(), market: Market.Futures);

        models.Says(Role.Strategist, plan);
        models.Says(Role.RiskManager, TeamAnswers.Review(verdict, (decimal?)reduced));

        // The Strategist answers in time, so its plan is reviewed, and the Risk Manager
        // answers later than the limit.
        models.Hold(Role.RiskManager);

        var run = cycles.RunAsync(Quarter);

        await models.StartedAsync(Role.RiskManager);
        cycles.Clock.Set(Quarter + Limit + TimeSpan.FromSeconds(1));
        models.Release(Role.RiskManager);
        await run.Within();

        var record = cycles.Records.Of(agent);

        Assert.Equal(outcome, record.Cycle.Outcome);
        Assert.Equal(RoleValidation.Valid, Assert.Single(record.Of(Role.RiskManager)).Validation);

        // What goes on of a stale plan is said with it, and a veto says nothing of the kind.
        Assert.Equal(
            goesOn is null ? [] : new[] { goesOn },
            cycles.Written(TeamLog.Area, TeamLog.Stale).Select(stale => stale.Text("GoesOn")));
    }

    [Fact]
    public async Task The_stale_limit_is_the_one_of_the_timeframe_of_the_agent()
    {
        using var cycles = new CyclesApplication(Hour, buildsPackages: true);
        var quarterly = cycles.Agents!.Give(Guid.NewGuid(), Timeframe.FifteenMinutes);
        var hourly = cycles.Agents!.Give(Guid.NewGuid(), Timeframe.OneHour);

        // A candle of each closes on the hour, and both decisions complete 150 seconds after:
        // later than the 100 seconds of 15m, and within the 200 seconds of 1H.
        await StrategistAnswersAsync(cycles, Hour, Hour + TimeSpan.FromSeconds(150));

        Assert.Equal(CycleOutcome.Stale, cycles.Records.Of(quarterly).Cycle.Outcome);
        Assert.Equal(CycleOutcome.Approved, cycles.Records.Of(hourly).Cycle.Outcome);

        var stale = Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Stale));

        Assert.Equal((quarterly.ToString(), "15m"), (stale.Text("AgentId"), stale.Text("Timeframe")));
    }

    [Fact]
    public async Task The_stale_limit_is_the_one_of_the_configuration_file()
    {
        // A file that gives a decision on 15m half a minute.
        using var cycles = new CyclesApplication(
            Quarter, buildsPackages: true, settings: file => file.With("Trading:StaleSeconds:15m", 30));
        var agent = cycles.Agents!.Give(Guid.NewGuid());

        await StrategistAnswersAsync(cycles, Quarter, Quarter + TimeSpan.FromSeconds(31));

        Assert.Equal(CycleOutcome.Stale, cycles.Records.Of(agent).Cycle.Outcome);
        Assert.Equal(30d, Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Stale))["StaleSeconds"].GetDouble());
    }

    /// <summary>
    /// Runs the cycles of a close with a Strategist that answers at a time of the test's
    /// choosing: its first call is held, the clock is moved, and the call is let go. Every call
    /// after it ends at once, by the same clock.
    /// </summary>
    private static async Task StrategistAnswersAsync(CyclesApplication cycles, DateTimeOffset closedAt, DateTimeOffset answersAt)
    {
        cycles.Models.Hold(Role.Strategist);

        var run = cycles.RunAsync(closedAt);

        await cycles.Models.StartedAsync(Role.Strategist);
        cycles.Clock.Set(answersAt);
        cycles.Models.Release(Role.Strategist);
        await run.Within();
    }

    /// <summary>An action as the log names it: <c>OpenLong</c>.</summary>
    private static string PlanOf(string action) => action switch
    {
        "open_long" => nameof(PlanAction.OpenLong),
        "open_short" => nameof(PlanAction.OpenShort),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No such action that opens a position."),
    };
}
