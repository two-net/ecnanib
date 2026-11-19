using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// A user can run a test cycle for an agent that produces a full decision record and places no
/// orders (FR-CYC-12). The record is told apart from the cycles that a candle close started. See
/// "A test cycle" in docs/cycles.md.
/// </summary>
/// <remarks>
/// The agents are those of the database, each with a whole team that the key of its user can
/// use. Unless a test says otherwise, the models, the keys, the records, and what places the
/// orders are stand-ins, and the exchange has all the market data.
/// </remarks>
public class TestCycleTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Area = "Ecnanib.DecisionEngine.TestCycles";

    private const string Runs =
        "User {UserId} runs a test cycle of agent {AgentId}, which is {AgentState}, for the close at {ClosedAt} ({Timeframe})";

    private const string Ended = "The test cycle of agent {AgentId} for the close at {ClosedAt} ended in {DurationMs} ms";

    private const string StoppedWithTheApplication =
        "The test cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms, with the application";

    private const string Failed =
        "The test cycle of agent {AgentId} for the close at {ClosedAt} failed after {DurationMs} ms, with no decision";

    private const string RolesBlock =
        "User {UserId} was refused a test cycle of agent {AgentId}: {RoleCount} of the roles of its team cannot work ({Roles})";

    private const string RunsAlready =
        "User {UserId} was refused a test cycle of agent {AgentId}: a test cycle of theirs runs already";

    private const string NoCandle =
        "The exchange did not give the candle of {Symbol} on {Market} in {TradingMode} that closed at {ClosedAt} ({Timeframe}), " +
        "for a test cycle of agent {AgentId}: {ExchangeCallOutcome}";

    /// <summary>A moment between two candle closes: seven and a half minutes after the close of 14:00.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 14, 7, 30, TimeSpan.Zero);

    /// <summary>The latest close of a candle of an hour then, and of one of 15 minutes.</summary>
    private static readonly DateTimeOffset LastHour = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The latest close of a candle of a day then: more than 14 hours earlier.</summary>
    private static readonly DateTimeOffset Midnight = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The stale limit of 1D in the file of the tests, in seconds.</summary>
    private const int LimitOfADay = 800;

    [Fact]
    public async Task A_test_cycle_produces_a_full_decision_record_that_is_told_apart_and_places_no_orders()
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles);

        var result = await RunAsync(cycles, user, agent);

        // It ran, and is recorded as every cycle is: under an id, for its agent and its user,
        // in the mode of the agent, for the latest closed candle of the agent's timeframe.
        var record = cycles.Records.Of(agent);

        Assert.Equal((TestCycleOutcome.Recorded, record.Cycle.Id), (result.Outcome, result.CycleId));
        Assert.Equal(
            (agent, user, TradingMode.Demo, LastHour, CycleOutcome.Approved),
            (record.Cycle.AgentId, record.Cycle.UserId, record.Cycle.Mode, record.Cycle.ClosedAt, record.Cycle.Outcome));

        // It is told apart from a cycle that a candle close started.
        Assert.Equal(CycleKind.Test, record.Cycle.Kind);

        // The record is full: the summary of the data package, every role output, the Risk
        // Guard results, and the final outcome (FR-CYC-10).
        var package = DataPackageSummary.Read(record.Cycle.DataPackage);

        Assert.NotNull(package);
        Assert.Equal(("BTCUSDT", Timeframe.OneHour, 1000m), (package.Symbol, package.Timeframe, package.Equity));
        Assert.Equal(
            Enum.GetValues<Role>().Select(role => (role, 1, RoleCallOutcome.Succeeded, RoleValidation.Valid)),
            record.Calls);
        Assert.Equal((PlanAction.OpenLong, GuardVerdict.Accepted, 1), (record.Check.Check.Action, record.Check.Check.Verdict, record.Check.Check.Sequence));
        Assert.Equal(Enum.GetValues<GuardRule>(), record.Check.Rules.Select(rule => rule.Rule));

        // It places no orders: what the Risk Guard accepted is handed to nothing, and nothing
        // of the agent is settled with the exchange.
        Assert.Empty(cycles.Orders.Placed);
        Assert.Empty(cycles.Orders.Settled);
        Assert.Null(record.Cycle.NoEntry);

        // The log says who ran it, for which agent and which close, and that it ended, with
        // the user, the agent, and the cycle on every event of it.
        var runs = Assert.Single(cycles.Written(Area, Runs));

        Assert.Equal(
            ("Information", user.ToString(), agent.ToString(), "Draft", "1H", record.Cycle.Id.ToString()),
            (runs.Level, runs.Text("UserId"), runs.Text("AgentId"), runs.Text("AgentState"), runs.Text("Timeframe"), runs.Text("CycleId")));
        Assert.Equal(record.Cycle.Id.ToString(), Assert.Single(cycles.Written(Area, Ended)).Text("CycleId"));
        Assert.Equal(record.Cycle.Id.ToString(), Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Decided)).Text("CycleId"));
    }

    [Theory]
    [InlineData(AgentState.Draft)]
    [InlineData(AgentState.Running)]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Halted)]
    [InlineData(AgentState.AttentionRequired)]
    [InlineData(AgentState.Stopped)]
    public async Task A_test_cycle_runs_for_an_agent_in_any_state_and_leaves_the_state_as_it_is(AgentState state)
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles, state: state);

        var result = await RunAsync(cycles, user, agent);

        Assert.Equal(TestCycleOutcome.Recorded, result.Outcome);
        Assert.Equal((CycleKind.Test, CycleOutcome.Approved), (cycles.Records.Of(agent).Cycle.Kind, cycles.Records.Of(agent).Cycle.Outcome));
        Assert.Empty(cycles.Orders.Placed);

        // The agent is as it was: a test cycle starts nothing, and halts nothing.
        Assert.Equal(state.ToString(), StoredAgent.Of(cycles.Application.Database, user).Single().State);
    }

    [Fact]
    public void Every_state_of_an_agent_is_one_that_a_test_cycle_is_run_in()
    {
        // The states that the theory above names are all there are: one that is added later is
        // added there, or this says so.
        Assert.Equal(
            [AgentState.Draft, AgentState.Running, AgentState.Paused, AgentState.Halted, AgentState.AttentionRequired, AgentState.Stopped],
            Enum.GetValues<AgentState>());
    }

    [Theory]
    [InlineData(Timeframe.FifteenMinutes, 14, CandleInterval.FifteenMinutes)]
    [InlineData(Timeframe.OneHour, 14, CandleInterval.OneHour)]
    [InlineData(Timeframe.FourHours, 12, CandleInterval.FourHours)]
    [InlineData(Timeframe.OneDay, 0, CandleInterval.OneDay)]
    public async Task A_test_cycle_is_for_the_latest_closed_candle_of_the_agents_timeframe(Timeframe timeframe, int hour, CandleInterval interval)
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles, timeframe: timeframe);
        var close = new DateTimeOffset(2026, 11, 18, hour, 0, 0, TimeSpan.Zero);

        await RunAsync(cycles, user, agent);

        // The close of the record is the latest UTC boundary of the timeframe, and the candles
        // of the data package reach through it: only closed candles are used (FR-CYC-1).
        var record = cycles.Records.Of(agent);

        Assert.Equal(close, record.Cycle.ClosedAt);
        Assert.Equal(close, DataPackageSummary.Read(record.Cycle.DataPackage)!.Series[0].Latest!.ClosedAt);

        // They were asked for once, as the candles of a close are.
        Assert.Single(cycles.Market!.Asked, asked => asked.Interval == interval);
    }

    [Fact]
    public void The_latest_close_of_a_timeframe_is_its_UTC_boundary_at_a_time_or_the_last_one_before_it()
    {
        Assert.Equal(LastHour, Timeframe.FifteenMinutes.LatestClose(Now));
        Assert.Equal(LastHour.AddMinutes(15), Timeframe.FifteenMinutes.LatestClose(LastHour.AddMinutes(29).AddSeconds(59)));
        Assert.Equal(LastHour, Timeframe.OneHour.LatestClose(Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero), Timeframe.FourHours.LatestClose(Now));
        Assert.Equal(Midnight, Timeframe.OneDay.LatestClose(Now));

        // A time that is a boundary is its own latest close, and it is said in UTC.
        Assert.All(Timeframes.All, timeframe => Assert.Equal(Midnight, timeframe.LatestClose(Midnight)));
        Assert.Equal(Midnight, Timeframe.OneDay.LatestClose(new DateTimeOffset(2026, 11, 18, 3, 30, 0, TimeSpan.FromHours(2))));
        Assert.Equal(TimeSpan.Zero, Timeframe.OneDay.LatestClose(new DateTimeOffset(2026, 11, 18, 3, 30, 0, TimeSpan.FromHours(2))).Offset);
    }

    [Fact]
    public async Task The_stale_limit_of_a_test_cycle_counts_from_the_moment_it_is_run_and_not_from_the_close_of_its_candle()
    {
        // The candle of a day closed at midnight, more than 14 hours ago: far later than the
        // stale limit of 800 seconds, by which a cycle that the close started could not open.
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles, timeframe: Timeframe.OneDay);

        await RunAsync(cycles, user, agent);

        // The plan to open a position is reviewed and goes on: the decision is in time.
        var record = cycles.Records.Of(agent);

        Assert.Equal((Midnight, CycleOutcome.Approved), (record.Cycle.ClosedAt, record.Cycle.Outcome));
        Assert.Single(cycles.Models.CallsOf(Role.RiskManager));
        Assert.Empty(cycles.Written(TeamLog.Area, TeamLog.Stale));

        // The Risk Guard counts the same: no time has passed since the cycle was run.
        var stale = record.Check.Rules.Single(rule => rule.Rule == GuardRule.NotStale);

        Assert.Equal((RuleOutcome.Passed, 0m, (decimal)LimitOfADay), (stale.Outcome, stale.Measured, stale.Threshold));
        Assert.Equal(GuardVerdict.Accepted, record.Check.Check.Verdict);
    }

    [Fact]
    public async Task A_test_cycle_whose_decision_completes_later_than_the_stale_limit_after_it_was_run_opens_no_position()
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles, timeframe: Timeframe.OneDay);

        // The Strategist answers one second later than the limit after the cycle was run.
        cycles.Models.Hold(Role.Strategist);

        var run = RunAsync(cycles, user, agent);

        await cycles.Models.StartedAsync(Role.Strategist);
        cycles.Clock.Set(Now.AddSeconds(LimitOfADay + 1));
        cycles.Models.Release(Role.Strategist);
        await run;

        // The cycle ends as stale, as one that a candle close started does: the plan is not
        // reviewed, and nothing goes on to the Risk Guard.
        var record = cycles.Records.Of(agent);

        Assert.Equal((CycleKind.Test, CycleOutcome.Stale), (record.Cycle.Kind, record.Cycle.Outcome));
        Assert.Empty(cycles.Models.CallsOf(Role.RiskManager));
        Assert.Empty(record.Checks);

        // The log says how late the decision was, counted from the moment the cycle was run.
        var stale = Assert.Single(cycles.Written(TeamLog.Area, TeamLog.Stale));

        Assert.Equal(
            (LimitOfADay + 1d, "1D", (double)LimitOfADay),
            (stale["CompletedAfterSeconds"].GetDouble(), stale.Text("Timeframe"), stale["StaleSeconds"].GetDouble()));
    }

    [Fact]
    public async Task The_Risk_Guard_holds_a_test_cycle_to_the_stale_limit_from_the_moment_it_was_run()
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles, timeframe: Timeframe.OneDay);

        // The team decides in time. As the Risk Guard reads its facts, the limit has passed.
        cycles.Market!.AsThePriceIsRead = () => cycles.Clock.Set(Now.AddSeconds(LimitOfADay + 1));

        await RunAsync(cycles, user, agent);

        // The outcome stays the decision of the team, and the Risk Guard rejects the plan: its
        // stale rule says the seconds since the cycle was run, and not the hours since the close.
        var record = cycles.Records.Of(agent);
        var stale = record.Check.Rules.Single(rule => rule.Rule == GuardRule.NotStale);

        Assert.Equal((CycleOutcome.Approved, GuardVerdict.Rejected), (record.Cycle.Outcome, record.Check.Check.Verdict));
        Assert.Equal((RuleOutcome.Failed, LimitOfADay + 1m, (decimal)LimitOfADay), (stale.Outcome, stale.Measured, stale.Threshold));
        Assert.Empty(cycles.Orders.Placed);
    }

    [Fact]
    public async Task No_test_cycle_runs_while_the_exchange_gives_no_candles()
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles);

        cycles.Market!.Candles = _ => StandInCandles.NoAnswer;

        var result = await RunAsync(cycles, user, agent);

        // Nothing was run: no model was called, and nothing is recorded.
        Assert.Equal((TestCycleOutcome.NoCandles, null), (result.Outcome, result.CycleId));
        Assert.Empty(cycles.Models.Calls);
        Assert.Empty(cycles.Records.Stored);

        var warned = Assert.Single(cycles.Written(Area, NoCandle));

        Assert.Equal(
            ("Warning", agent.ToString(), "BTCUSDT", "1H", "Failed"),
            (warned.Level, warned.Text("AgentId"), warned.Text("Symbol"), warned.Text("Timeframe"), warned.Text("ExchangeCallOutcome")));
        Assert.Empty(cycles.Written(Area, Runs));

        // The user is not kept from another: the exchange answers again.
        cycles.Market.Candles = null;

        Assert.Equal(TestCycleOutcome.Recorded, (await RunAsync(cycles, user, agent)).Outcome);
    }

    [Fact]
    public async Task A_team_that_is_not_whole_runs_no_test_cycle_and_no_model_is_called()
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles, team: false);

        // One role of four has a model.
        await cycles.Application.GiveRoleAsync(user, agent, Role.MarketAnalyst, Now.AddHours(-1));

        var result = await RunAsync(cycles, user, agent);

        // An agent runs with its whole team (FR-TEAM-1): the answer names each role that stands
        // in the way, as a start of the agent does.
        Assert.Equal(TestCycleOutcome.RolesBlock, result.Outcome);
        Assert.Equal(
            [
                new RoleBlock(Role.SentimentAnalyst, RoleProblem.Missing),
                new RoleBlock(Role.Strategist, RoleProblem.Missing),
                new RoleBlock(Role.RiskManager, RoleProblem.Missing),
            ],
            result.Roles);

        // No model is paid for a cycle that cannot be completed, and nothing is recorded.
        Assert.Empty(cycles.Models.Calls);
        Assert.Empty(cycles.Team!.Keys.Asked);
        Assert.Empty(cycles.Records.Stored);
        Assert.Empty(cycles.Market!.Asked);

        var refused = Assert.Single(cycles.Written(Area, RolesBlock));

        Assert.Equal((3, "SentimentAnalyst: Missing, Strategist: Missing, RiskManager: Missing"), (refused["RoleCount"].GetInt32(), refused.Text("Roles")));
    }

    [Fact]
    public async Task A_user_runs_one_test_cycle_at_a_time_and_keeps_no_other_user_waiting()
    {
        using var cycles = Application();
        var (ada, first) = await AgentAsync(cycles);
        var second = await cycles.Application.GiveAgentAsync(ada, Now.AddHours(-1), name: "Second");
        var (grace, hers) = await AgentAsync(cycles, Grace);

        // The first test cycle of Ada runs: the call of its Market Analyst is under way.
        cycles.Models.Hold(Role.MarketAnalyst);

        var running = RunAsync(cycles, ada, first);

        await cycles.Models.StartedAsync(Role.MarketAnalyst);

        // Another one of hers is refused at once, of another agent and of the same.
        Assert.Equal(TestCycleOutcome.AlreadyRunning, (await RunAsync(cycles, ada, second)).Outcome);
        Assert.Equal(TestCycleOutcome.AlreadyRunning, (await RunAsync(cycles, ada, first)).Outcome);
        Assert.Single(cycles.Models.CallsOf(Role.MarketAnalyst));
        Assert.Equal([second.ToString(), first.ToString()], cycles.Written(Area, RunsAlready).Select(refused => refused.Text("AgentId")));

        // Grace is not held up by it: her test cycle starts beside it.
        var beside = RunAsync(cycles, grace, hers);

        await cycles.Models.StartedAsync(Role.MarketAnalyst, calls: 2);

        cycles.Models.Release(Role.MarketAnalyst);

        Assert.Equal(TestCycleOutcome.Recorded, (await running).Outcome);
        Assert.Equal(TestCycleOutcome.Recorded, (await beside).Outcome);

        // Once hers has ended, Ada runs another.
        Assert.Equal(TestCycleOutcome.Recorded, (await RunAsync(cycles, ada, second)).Outcome);
        Assert.Equal(new[] { first, hers, second }.Order(), cycles.Records.Stored.Select(stored => stored.Cycle.AgentId).Order());
    }

    [Fact]
    public async Task A_test_cycle_of_an_agent_of_another_user_is_not_found()
    {
        using var cycles = Application();
        var (_, agent) = await AgentAsync(cycles);
        var grace = await cycles.Application.RegisterVerifiedAsync(Grace);

        // Grace asks for a test cycle of the agent of Ada, and for one of an agent that is not there.
        Assert.Equal(TestCycleOutcome.NotFound, (await RunAsync(cycles, grace.Id, agent)).Outcome);
        Assert.Equal(TestCycleOutcome.NotFound, (await RunAsync(cycles, grace.Id, Guid.CreateVersion7())).Outcome);

        Assert.Empty(cycles.Models.Calls);
        Assert.Empty(cycles.Records.Stored);
        Assert.Empty(cycles.Market!.Asked);
        Assert.Empty(cycles.Written(Area));
    }

    [Fact]
    public async Task A_test_cycle_is_stopped_with_the_application_and_recorded_as_stopped()
    {
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles);

        cycles.Models.Hold(Role.MarketAnalyst);

        var run = RunAsync(cycles, user, agent);

        await cycles.Models.StartedAsync(Role.MarketAnalyst);

        // The application stops while the cycle runs. The run of this test is no request that
        // the application waits for as it stops, so what it answers is not asked: the record
        // and the log say what became of the cycle.
        cycles.Application.BeginStopping();
        await run;

        // The cycle was told to stop, and is recorded as one that was stopped, with the call
        // that was under way: its provider may have charged for it.
        var record = cycles.Records.Of(agent);

        Assert.Equal((CycleKind.Test, CycleOutcome.Stopped), (record.Cycle.Kind, record.Cycle.Outcome));
        Assert.Equal(RoleCallOutcome.Canceled, Assert.Single(record.Of(Role.MarketAnalyst)).Outcome);
        Assert.Empty(cycles.Models.CallsOf(Role.Strategist));
        Assert.Empty(cycles.Orders.Placed);

        var stopped = Assert.Single(cycles.Written(Area, StoppedWithTheApplication));

        Assert.Equal(("Information", agent.ToString(), record.Cycle.Id.ToString()), (stopped.Level, stopped.Text("AgentId"), stopped.Text("CycleId")));

        // It ended: its user is not kept from another one by a cycle that is over.
        cycles.Models.Release(Role.MarketAnalyst);
    }

    [Fact]
    public async Task A_test_cycle_that_fails_is_recorded_as_failed_and_logged_once()
    {
        // The agent trades on Futures, and the exchange does not say the mark price: its data
        // package cannot be built.
        using var cycles = Application();
        var (user, agent) = await AgentAsync(cycles, market: Market.Futures);

        cycles.Market!.MarkPriceOutcome = ExchangeCallOutcome.TimedOut;

        await RunAsync(cycles, user, agent);

        // The cycle ended with no decision, and its record says so, as that of every cycle does.
        var record = cycles.Records.Of(agent);

        Assert.Equal((CycleKind.Test, CycleOutcome.Failed), (record.Cycle.Kind, record.Cycle.Outcome));
        Assert.Null(record.Cycle.DataPackage);
        Assert.Empty(cycles.Models.Calls);

        // The error is logged once, with the agent and the cycle, as the job logs a cycle that failed.
        var failed = Assert.Single(cycles.Written(Area, Failed));

        Assert.Equal(("Error", agent.ToString(), record.Cycle.Id.ToString()), (failed.Level, failed.Text("AgentId"), failed.Text("CycleId")));
    }

    [Fact]
    public async Task What_places_the_orders_refuses_a_test_cycle()
    {
        // The application with the orders themselves, and a Binance of the test behind the gateway.
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // A plan that the Risk Guard accepts, and a test cycle of the agent.
        var accepted = GuardCheck.Of(GuardCases.OpenLong);
        var cycle = application.CycleOf(agent) with { TestedAt = application.Clock.GetUtcNow() };

        Assert.True(accepted.IsAccepted);
        Assert.True(cycle.IsTest);

        // What runs a test cycle hands its plan to nothing. Whatever hands one over all the
        // same is refused where an order is made: nothing is written, and nothing is sent.
        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => application.Orders.PlaceAsync(cycle, accepted, TestContext.Current.CancellationToken));

        Assert.StartsWith("A test cycle places no order.", refused.Message);
        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Empty(await application.PositionsOfAsync(agent.Id));

        // The same plan of a cycle that a candle close started is placed.
        Assert.Null(await application.PlaceAsync(agent, GuardCases.OpenLong));
        Assert.NotEmpty(application.Binance.Asked);
    }

    [Fact]
    public async Task A_test_cycle_settles_no_order_and_halts_no_agent_that_has_reached_a_loss_limit()
    {
        // The agent of LossLimitHaltTests, with the orders and the loss limits themselves: it
        // bought at 60000, and its user closed the position at 54000. That is more than its
        // daily loss limit of 5 percent, which its next cycle would find, and halt it for.
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        Assert.Equal(LossLimitVerdict.Within, await LossLimitHaltTests.EvaluateAsync(application, agent));
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        application.Binance.Price = 54000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        await application.Application.GiveTeamAsync(agent.UserId, agent.Id, application.Clock.GetUtcNow());

        var running = await application.Application.RunningAgents.FindAsync(agent.Id, TestContext.Current.CancellationToken);
        var asked = application.Binance.Asked.Count;
        var snapshots = (await application.SnapshotsOfAsync(agent.Id)).Count;

        // A test cycle of it. The exchange of this test has no candles, so the cycle ends where
        // its data package is built: after where a cycle settles and evaluates.
        var cycle = new CycleStart(
            Guid.CreateVersion7(),
            running!.Agent,
            running.Roles,
            application.Clock.GetUtcNow().AddSeconds(-30),
            new ExchangeCandles(ExchangeCallOutcome.Failed, [], null),
            TestedAt: application.Clock.GetUtcNow());

        await Assert.ThrowsAnyAsync<Exception>(
            () => application.WhileTimePassesAsync(application.Application.Cycle.RunAsync(cycle, TestContext.Current.CancellationToken)));

        // The agent runs as it did: nothing halted it, and nobody was told that it was.
        var after = await application.AgentAsync(agent.Id);

        Assert.Equal((AgentState.Running, AgentStateCause.UserStarted), (after.State, after.StateCause));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));

        // Nothing was asked of the exchange about its orders, and no equity of it was recorded.
        Assert.Equal(asked, application.Binance.Asked.Count);
        Assert.Equal(snapshots, (await application.SnapshotsOfAsync(agent.Id)).Count);

        // The cycle is recorded as a test cycle that failed, and not as one that halted its agent.
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        var recorded = await context.Set<DecisionCycle>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((cycle.CycleId, CycleKind.Test, CycleOutcome.Failed), (recorded.Id, recorded.Kind, recorded.Outcome));

        // A cycle that a candle close starts halts it, on the same records.
        var scheduled = cycle with { CycleId = Guid.CreateVersion7(), TestedAt = null };

        await application.WhileTimePassesAsync(application.Application.Cycle.RunAsync(scheduled, TestContext.Current.CancellationToken));

        Assert.Equal(AgentState.Halted, (await application.AgentAsync(agent.Id)).State);
    }

    /// <summary>
    /// The application with the agents of the database, the inside of a cycle as the application
    /// has it, and stand-ins for the models, the keys, the records, and what places the orders.
    /// Its clock stands between two candle closes, so the job that starts the cycles of a close
    /// has nothing to do.
    /// </summary>
    private static CyclesApplication Application() => new(Now, storedAgents: true, buildsPackages: true);

    /// <summary>Gives a new user an agent with a whole team that their key can use, unless the test says that it has none.</summary>
    private static async Task<(Guid UserId, Guid AgentId)> AgentAsync(
        CyclesApplication cycles,
        string email = Ada,
        AgentState state = AgentState.Draft,
        Timeframe timeframe = Timeframe.OneHour,
        Market market = Market.Spot,
        bool team = true)
    {
        var user = await cycles.Application.RegisterVerifiedAsync(email);
        var agent = await cycles.Application.GiveAgentAsync(
            user.Id, Now.AddHours(-1), state: state, market: market, team: team, timeframe: timeframe);

        return (user.Id, agent);
    }

    /// <summary>Runs a test cycle of an agent for a user, as the page does, and does not wait for it longer than half a minute.</summary>
    private static Task<TestCycleResult> RunAsync(CyclesApplication cycles, Guid userId, Guid agentId) =>
        cycles.Application.TestCycles
            .RunAsync(userId, agentId, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
}
