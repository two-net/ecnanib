using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The cycles of a close are over before the next close. A cycle that could not start within
/// five minutes of its close is skipped, and one that still runs half a minute before the next
/// close is stopped, so that a queue that cannot keep up, or a cycle that hangs, never costs
/// every agent its next cycle. When the application stops, its cycles stop with it, and it waits
/// for them (NFR-REL-5). See "When time runs out" in docs/cycles.md.
/// </summary>
public class CycleOverloadTests
{
    // Between two quarter hours, so that the job is not due when the application starts.
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 10, 7, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextQuarter = Quarter.AddMinutes(15);

    [Fact]
    public async Task A_cycle_that_could_not_start_within_five_minutes_is_skipped()
    {
        using var application = new CyclesApplication(Quarter, maxPerProvider: 1);
        var agents = application.Agents!;

        var first = agents.Give(Guid.NewGuid());
        agents.Give(Guid.NewGuid());

        // The first cycle goes on, and the second waits for its place.
        application.Cycles.Holds = true;
        var run = application.RunAsync(Quarter);
        await application.Cycles.StartedAsync(1);

        // Five minutes after the close its turn has not come.
        application.Clock.Set(Quarter.AddMinutes(5));
        await Wait.UntilAsync(
            () => application.Written(CyclesLog.Area, CyclesLog.NotStartedInTime).Count == 1, "the cycle that waits to be skipped");

        // It does not start later either, when the place is free.
        application.Cycles.ReleaseAll();
        await run.Within();

        Assert.Equal([first], application.Cycles.StartedFor);

        var skipped = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.NotStartedInTime));
        Assert.Equal("Warning", skipped.Level);
        Assert.Equal(1, skipped["CycleCount"].GetInt32());
        Assert.Equal(Quarter, skipped["ClosedAt"].GetDateTimeOffset());
        Assert.Equal(1, application.SummaryOf(Quarter)["TooLate"].GetInt32());
    }

    [Fact]
    public async Task A_run_that_begins_five_minutes_after_its_close_starts_no_cycle()
    {
        // The scheduler starts a run that is late for its boundary, and tells it the boundary:
        // the machine slept, or the start could not be written for that long.
        using var application = new CyclesApplication(Quarter.AddMinutes(5));
        application.Agents!.Give(Guid.NewGuid());

        await application.RunAsync(Quarter).Within();

        Assert.Empty(application.Cycles.Started);
        Assert.Empty(application.Candles.Asked);

        var late = Assert.Single(application.Written(), written => written.Level == "Warning");
        Assert.Equal(CyclesLog.RunTooLate, late.Template);
        Assert.Equal(Quarter, late["ClosedAt"].GetDateTimeOffset());
    }

    [Fact]
    public async Task A_cycle_that_still_runs_as_the_next_close_nears_is_stopped_and_the_next_close_starts_its_cycles()
    {
        using var application = new CyclesApplication(Start);
        var agent = application.Agents!.Give(Guid.NewGuid());
        await application.SchedulerStartedAsync();

        // The scheduler starts the cycle at the close, and the cycle does not end.
        application.Cycles.Holds = true;
        application.Clock.Set(Quarter);
        await application.Cycles.StartedAsync(1);

        // Half a minute before the next close it is stopped, through its token.
        application.Clock.Set(NextQuarter.AddSeconds(-30));
        await Wait.UntilAsync(() => application.Cycles.Stopped.Count == 1, "the cycle to be stopped");
        Assert.Equal([agent], application.Cycles.Stopped);

        // So the run is over, and the scheduler has it as done, before the next close.
        await Wait.UntilAsync(
            () => StoredJob.Of(application.Application.Database, "decision-cycles")?.Outcome == "Succeeded",
            "the run of the first close to be over");

        // The next close starts the next cycle of the agent.
        application.Clock.Set(NextQuarter);
        await application.Cycles.StartedAsync(2);
        Assert.Equal([Quarter, NextQuarter], application.Cycles.Started.Select(cycle => cycle.ClosedAt));
        Assert.All(application.Cycles.Started, cycle => Assert.Equal(agent, cycle.Agent.Id));

        // The job says what it stopped, and why, with the agent and the cycle.
        var still = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.StillRunning));
        Assert.Equal(("Warning", 1), (still.Level, still["CycleCount"].GetInt32()));

        var stopped = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.StoppedForTheNextClose));
        Assert.Equal("Warning", stopped.Level);
        Assert.Equal(agent.ToString(), stopped.Text("AgentId"));
        Assert.Equal(application.Cycles.Started[0].CycleId.ToString(), stopped.Text("CycleId"));
        Assert.Equal(1, application.SummaryOf(Quarter)["OutOfTime"].GetInt32());

        // No boundary was lost to the run before it: the scheduler has nothing to warn of.
        Assert.DoesNotContain(application.Written(SchedulerLog.Area), written => written.Level is "Warning" or "Error" or "Fatal");
    }

    [Fact]
    public async Task Stopping_the_application_stops_its_cycles_and_waits_for_them()
    {
        using var application = new CyclesApplication(Start);
        var agent = application.Agents!.Give(Guid.NewGuid());
        await application.SchedulerStartedAsync();

        application.Cycles.Holds = true;
        application.Clock.Set(Quarter);
        await application.Cycles.StartedAsync(1);

        // The application stops while the cycle runs.
        application.Stop();

        // The cycle was told to stop, and the application waited until it had: the run is
        // recorded as stopped, and not as one that never finished.
        Assert.Equal([agent], application.Cycles.Stopped);

        var job = StoredJob.Of(application.Application.Database, "decision-cycles");
        Assert.Equal("Stopped", job?.Outcome);
        Assert.NotNull(job?.FinishedAt);
    }
}
