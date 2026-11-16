using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// A cycle starts when a candle of the agent's timeframe closes, on UTC boundaries (FR-CYC-1).
/// Only agents in the Running state get cycles (SRS 3.3). Cycles missed while the service was
/// down are skipped, not replayed: after a restart the system waits for the next candle close
/// (FR-CYC-8). The agents are those of the database here, read as the application reads them.
/// See docs/cycles.md.
/// </summary>
public class CycleScheduleTests
{
    private const string Ada = "ada@example.com";

    // Between two quarter hours, so that the job is not due when the application starts.
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 10, 7, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

    /// <summary>A moment, and the codes of the timeframes of which a candle closes then.</summary>
    public static TheoryData<DateTimeOffset, string[]> Closes => new()
    {
        { new DateTimeOffset(2026, 11, 16, 10, 15, 0, TimeSpan.Zero), ["15m"] },
        { new DateTimeOffset(2026, 11, 16, 10, 45, 0, TimeSpan.Zero), ["15m"] },
        { new DateTimeOffset(2026, 11, 16, 11, 0, 0, TimeSpan.Zero), ["15m", "1H"] },
        { new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero), ["15m", "1H", "4H"] },
        { new DateTimeOffset(2026, 11, 16, 20, 0, 0, TimeSpan.Zero), ["15m", "1H", "4H"] },
        { new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), ["15m", "1H", "4H", "1D"] },

        // Between two quarter hours no candle closes.
        { new DateTimeOffset(2026, 11, 16, 10, 7, 0, TimeSpan.Zero), [] },
    };

    [Theory]
    [MemberData(nameof(Closes))]
    public async Task A_cycle_starts_for_each_running_agent_when_a_candle_of_its_timeframe_closes(DateTimeOffset close, string[] timeframes)
    {
        using var application = new CyclesApplication(close, storedAgents: true);
        var user = await application.Application.RegisterVerifiedAsync(Ada);

        // One agent on each timeframe, each on a symbol, a market, and an environment of its own.
        var agents = new Dictionary<string, Guid>
        {
            ["15m"] = await GiveAsync(application, user.Id, "Quarter", Timeframe.FifteenMinutes),
            ["1H"] = await GiveAsync(application, user.Id, "Hour", Timeframe.OneHour, symbol: "SOLUSDT"),
            ["4H"] = await GiveAsync(application, user.Id, "Four hours", Timeframe.FourHours, market: Market.Futures),
            ["1D"] = await GiveAsync(application, user.Id, "Day", Timeframe.OneDay, mode: TradingMode.Live),
        };

        await application.RunAsync(close).Within();

        // A cycle for each agent whose candle closed, for that close, and for no other agent.
        Assert.Equal(timeframes.Select(code => agents[code]).Order(), application.Cycles.StartedFor.Order());
        Assert.All(application.Cycles.Started, cycle =>
        {
            Assert.Equal(close, cycle.ClosedAt);
            Assert.Equal(close, cycle.Candles.Candles[^1].ClosedAt);
        });

        // The candles are those of the agent's own timeframe, market, and environment.
        Assert.Equal(
            timeframes.Order(StringComparer.Ordinal),
            application.Candles.Asked.Select(asked => Timeframes.All.Single(timeframe => timeframe.ToString() == asked.Interval.ToString()).Code())
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Only_an_agent_that_is_running_gets_a_cycle()
    {
        using var application = new CyclesApplication(Quarter, storedAgents: true);
        var user = await application.Application.RegisterVerifiedAsync(Ada);

        // An agent in each state of the specification, all on the timeframe that closes. A user
        // has one active agent for a market, a symbol, and an environment, so each has its own.
        (Market Market, string Symbol, TradingMode Mode)[] places =
        [
            (Market.Spot, "BTCUSDT", TradingMode.Demo),
            (Market.Spot, "SOLUSDT", TradingMode.Demo),
            (Market.Futures, "BTCUSDT", TradingMode.Demo),
            (Market.Spot, "BTCUSDT", TradingMode.Live),
            (Market.Spot, "SOLUSDT", TradingMode.Live),
            (Market.Futures, "BTCUSDT", TradingMode.Live),
        ];

        var agents = new Dictionary<AgentState, Guid>();
        foreach (var (state, place) in Enum.GetValues<AgentState>().Zip(places))
        {
            agents[state] = await application.Application.GiveAgentAsync(
                user.Id,
                Start,
                name: state.ToString(),
                state: state,
                market: place.Market,
                symbol: place.Symbol,
                mode: place.Mode,
                timeframe: Timeframe.FifteenMinutes);
        }

        await application.RunAsync(Quarter).Within();

        // Only the one that runs (SRS 3.3). What says so is what the pages of an agent go by.
        Assert.Equal([agents[AgentState.Running]], application.Cycles.StartedFor);
        Assert.Equal(
            [.. agents.Where(agent => AgentStates.RunsCycles(agent.Key)).Select(agent => agent.Value)],
            application.Cycles.StartedFor);

        // An agent that does not run is not asked about either: no candles are read for it.
        Assert.Equal(
            [new AskedCandles(Market.Spot, TradingMode.Demo, "SOLUSDT", Ecnanib.ExchangeGateway.CandleInterval.FifteenMinutes)],
            application.Candles.Asked);
    }

    [Fact]
    public async Task A_cycle_is_told_its_agent_its_roles_its_close_and_its_candles_and_logs_under_its_own_id()
    {
        using var application = new CyclesApplication(Quarter, storedAgents: true);
        var user = await application.Application.RegisterVerifiedAsync(Ada);
        var agent = await GiveAsync(application, user.Id, "Swing by Ada", Timeframe.FifteenMinutes);

        await application.RunAsync(Quarter).Within();

        var cycle = Assert.Single(application.Cycles.Started);

        // The agent as it is stored, with its settings, and whose it is.
        Assert.Equal((agent, user.Id), (cycle.Agent.Id, cycle.Agent.UserId));
        Assert.Equal(
            (Market.Spot, "BTCUSDT", TradingMode.Demo, Timeframe.FifteenMinutes, AgentState.Running),
            (cycle.Agent.Market, cycle.Agent.Symbol, cycle.Agent.Mode, cycle.Agent.Timeframe, cycle.Agent.State));
        Assert.Equal(1000m, cycle.Agent.AllocatedCapital);

        // The roles of its team, in their order, each with its provider and its model.
        Assert.Equal(Enum.GetValues<Role>(), cycle.Roles.Select(role => role.Role));
        Assert.All(cycle.Roles, role => Assert.Equal(("claude", StoredAgentRows.TeamModel), (role.Provider.Value, role.Identifier)));

        // The close that it is for, and the closed candles through it.
        Assert.Equal(Quarter, cycle.ClosedAt);
        Assert.Equal(Quarter, cycle.Candles.Candles[^1].ClosedAt);

        // An id of its own, which everything that is logged for it carries, with the agent and
        // its user (NFR-LOG-3).
        Assert.NotEqual(Guid.Empty, cycle.CycleId);

        var written = application.Written().Where(written => written.Has("CycleId")).ToList();
        Assert.Equal([CyclesLog.Started, CyclesLog.Ended], written.Select(written => written.Template));
        Assert.All(written, written =>
        {
            Assert.Equal("Information", written.Level);
            Assert.Equal(cycle.CycleId.ToString(), written.Text("CycleId"));
            Assert.Equal(agent.ToString(), written.Text("AgentId"));
            Assert.Equal(user.Id.ToString(), written.Text("UserId"));
            Assert.Equal(Quarter, written["ClosedAt"].GetDateTimeOffset());
        });
        Assert.Equal("15m", written[0].Text("Timeframe"));

        // The close has one summary, and nothing to warn of.
        var summary = application.SummaryOf(Quarter);
        Assert.Equal(("Information", 1), (summary.Level, summary["Ended"].GetInt32()));
        Assert.DoesNotContain(application.Written(), written => written.Level is "Warning" or "Error" or "Fatal");

        // What a user named an agent is in no log event.
        Assert.DoesNotContain("Swing by Ada", application.Application.LogText());
    }

    [Fact]
    public async Task The_scheduler_starts_the_cycles_at_the_close_and_makes_up_for_no_close_that_passed_while_the_service_was_down()
    {
        Guid agent;
        Persistence.TestDatabase database;

        using (var first = new CyclesApplication(Start, storedAgents: true))
        {
            database = first.Application.Database;

            var user = await first.Application.RegisterVerifiedAsync(Ada);
            agent = await GiveAsync(first, user.Id, "Quarter", Timeframe.FifteenMinutes);
            await first.SchedulerStartedAsync();

            // Nothing starts between two closes.
            Assert.Empty(first.Cycles.Started);

            // At the close the scheduler runs the job, and the cycle of the agent starts.
            first.Clock.Set(Quarter.AddSeconds(1));
            await first.Cycles.StartedAsync(1);
            Assert.Equal((agent, Quarter), (first.Cycles.Started[0].Agent.Id, first.Cycles.Started[0].ClosedAt));

            await Wait.UntilAsync(
                () => StoredJob.Of(database, "decision-cycles")?.Outcome == "Succeeded", "the run of the first close to be over");
        }

        // The service is down over the closes at 10:30 and at 10:45, and is back at 10:50.
        using var second = new CyclesApplication(Quarter.AddMinutes(35), storedAgents: true, database: database);
        await second.SchedulerStartedAsync();

        // The closes that passed start no cycles: they are skipped, and not replayed (FR-CYC-8).
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Empty(second.Cycles.Started);
        Assert.Empty(second.Candles.Asked);

        // The system waits for the next candle close, and starts the cycle of that close.
        var next = Quarter.AddMinutes(45);
        second.Clock.Set(next.AddSeconds(1));
        await second.Cycles.StartedAsync(1);

        var cycle = Assert.Single(second.Cycles.Started);
        Assert.Equal((agent, next), (cycle.Agent.Id, cycle.ClosedAt));
        Assert.Equal(next, cycle.Candles.Candles[^1].ClosedAt);

        Assert.DoesNotContain(second.Written(SchedulerLog.Area), written => written.Level is "Warning" or "Error" or "Fatal");
    }

    private static Task<Guid> GiveAsync(
        CyclesApplication application,
        Guid userId,
        string name,
        Timeframe timeframe,
        string symbol = "BTCUSDT",
        Market market = Market.Spot,
        TradingMode mode = TradingMode.Demo) =>
        application.Application.GiveAgentAsync(
            userId, Start, name, AgentState.Running, market, symbol, mode, AgentStateCause.UserStarted, timeframe: timeframe);
}
