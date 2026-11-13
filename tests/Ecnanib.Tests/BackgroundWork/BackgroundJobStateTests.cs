using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// Background work keeps its state in the database, so it continues correctly after the
/// application restarts (NFR-ARC-4): an interval goes on from the previous run, boundaries that
/// passed while the application was down are skipped (FR-CYC-8), and a run that was cut short is
/// neither lost nor repeated blindly.
/// </summary>
/// <remarks>
/// The scheduler goes by a clock the test sets, and it looks at its jobs in the order they are
/// registered. So when a job registered after another one has run, the scheduler has looked at
/// the other one too, at that time.
/// </remarks>
public class BackgroundJobStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 7, 0, TimeSpan.Zero);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);

    [Fact]
    public async Task A_start_is_stored_before_the_job_runs_and_its_outcome_after()
    {
        var database = PostgresServer.CreateDatabase();
        StoredJob? duringTheRun = null;
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Daily), (_, _) =>
        {
            duringTheRun = StoredJob.Of(database, "sync");

            return Task.CompletedTask;
        });
        using var application = ApplicationWithJobs.On(database, new TestClock(Start), job);

        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome is not null, "the outcome of the run in the database");

        Assert.Equal(new StoredJob(Start, FinishedAt: null, Outcome: null), duringTheRun);
        Assert.Equal(new StoredJob(Start, FinishedAt: Start, Outcome: "Succeeded"), StoredJob.Of(database, "sync"));

        // A job that has never run is due at once, and is told so.
        Assert.Equal([new BackgroundJobRun(Start)], job.Runs);
    }

    [Fact]
    public async Task After_a_restart_an_interval_goes_on_from_the_previous_run()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Daily));

        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Succeeded(database, "sync");
        }

        // An hour short of a day later, the application starts again.
        clock.Set(Start.AddHours(23));
        var later = new RecordingJob("later", BackgroundJobSchedule.Every(Daily));
        using var second = ApplicationWithJobs.On(database, clock, job, later);
        _ = second.Services;

        // Not on the start: the interval has not passed.
        await Wait.UntilAsync(() => later.Runs.Count == 1, "the job that is looked at after the one under test");
        Assert.Single(job.Runs);

        // And not a day after the restart: a day after the previous run.
        clock.Set(Start.AddHours(24));
        await Wait.UntilAsync(() => job.Runs.Count == 2, "the run that is due a day after the previous one");

        Assert.Equal(Start.AddHours(24), job.Runs[1].ScheduledFor);
        await Succeeded(database, "sync");
        Assert.Equal(Start.AddHours(24), StoredJob.Of(database, "sync")!.StartedAt);
        Assert.Empty(SchedulerLog.Trouble(second));
    }

    [Fact]
    public async Task A_job_that_became_overdue_while_the_application_was_down_runs_once_at_the_next_start()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Daily));
        var hourly = new RecordingJob("hourly", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));

        using (var first = ApplicationWithJobs.On(database, clock, job, hourly))
        {
            _ = first.Services;
            await Succeeded(database, "sync");
            await Succeeded(database, "hourly");
        }

        // Five days pass before the application starts again.
        clock.Set(Start.AddDays(5));
        using var second = ApplicationWithJobs.On(database, clock, job, hourly);
        _ = second.Services;

        await Wait.UntilAsync(() => job.Runs.Count == 2, "the overdue run at the start");
        Assert.Equal(Start.AddHours(24), job.Runs[1].ScheduledFor);

        // One run makes up for the five days. An hour on, the scheduler has looked again.
        clock.Set(Start.AddDays(5).AddHours(1));
        await Wait.UntilAsync(() => hourly.Runs.Count == 3, "the hourly job an hour after the start");

        Assert.Equal(2, job.Runs.Count);
        Assert.Equal(Start.AddDays(5), StoredJob.Of(database, "sync")!.StartedAt);
    }

    [Fact]
    public async Task Boundaries_missed_while_the_application_was_down_are_skipped()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var cycle = new RecordingJob("cycle", BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15)));
        var hourly = new RecordingJob("hourly", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));

        using (var first = ApplicationWithJobs.On(database, clock, cycle, hourly))
        {
            _ = first.Services;

            // At 12:07 the job has never run, and still it waits for a boundary.
            await Wait.UntilAsync(() => hourly.Runs.Count == 1, "the hourly job at the first start");
            Assert.Empty(cycle.Runs);

            clock.Set(At(12, 15));
            await Succeeded(database, "cycle");
        }

        // Down from 12:15 to 13:07: the boundaries at 12:30, 12:45, and 13:00 pass.
        clock.Set(At(13, 7));
        using var second = ApplicationWithJobs.On(database, clock, cycle, hourly);
        _ = second.Services;

        await Wait.UntilAsync(() => hourly.Runs.Count == 2, "the hourly job at the second start");
        Assert.Single(cycle.Runs);

        clock.Set(At(13, 15));
        await Wait.UntilAsync(() => cycle.Runs.Count == 2, "the run at the next boundary");

        Assert.Equal([At(12, 15), At(13, 15)], cycle.Runs.Select(run => run.ScheduledFor));
        Assert.Empty(SchedulerLog.Trouble(second));

        static DateTimeOffset At(int hour, int minute) => new(2026, 11, 11, hour, minute, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task A_run_that_was_stopped_with_the_application_runs_again_at_the_next_start()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Daily), RecordingJob.UntilStopped);

        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Wait.UntilAsync(() => job.Runs.Count == 1, "the run that the stop cuts short");

            // Stopped here, so that its log can be read before it goes with the application.
            await first.Services.GetRequiredService<IHost>().StopAsync(TestContext.Current.CancellationToken);

            // Stopping the application is not trouble. The run says that it was stopped.
            Assert.Empty(SchedulerLog.Trouble(first));
            Assert.Equal(1, SchedulerLog.Count(first, SchedulerLog.StoppedRun));
        }

        Assert.Equal(1, job.Stopped);
        Assert.Equal(new StoredJob(Start, FinishedAt: Start, Outcome: "Stopped"), StoredJob.Of(database, "sync"));

        // A minute later the application is back, and the job gets to do its work.
        clock.Set(Start.AddMinutes(1));
        job.Work = null;
        using var second = ApplicationWithJobs.On(database, clock, job);
        _ = second.Services;

        await Succeeded(database, "sync");

        Assert.Equal(2, job.Runs.Count);
        Assert.Equal(Start.AddMinutes(1), StoredJob.Of(database, "sync")!.StartedAt);

        // A run that was stopped is not one that never finished: nothing is reported.
        Assert.Empty(SchedulerLog.Trouble(second));
    }

    [Fact]
    public async Task A_boundary_that_passed_while_the_machine_slept_is_skipped()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var cycle = new RecordingJob("cycle", BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15)));
        var hourly = new RecordingJob("hourly", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));
        using var application = ApplicationWithJobs.On(database, clock, cycle, hourly);

        _ = application.Services;
        await Wait.UntilAsync(() => hourly.Runs.Count == 1, "the hourly job at the start");

        // The application runs on, and the next time it looks at the clock an hour has passed:
        // the boundaries at 12:15, 12:30, 12:45, and 13:00 with it.
        clock.Set(Start.AddHours(1));
        await Wait.UntilAsync(() => hourly.Runs.Count == 2, "the hourly job after the hour");

        // As after a restart, none of them is made up for (FR-CYC-8), and that is said.
        Assert.Empty(cycle.Runs);
        var skipped = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal("Warning", skipped.Level);
        Assert.Equal(SchedulerLog.Late, skipped.Template);
        Assert.Equal(Start.AddMinutes(8), skipped["DueAt"].GetDateTimeOffset());

        clock.Set(Start.AddMinutes(68));
        await Wait.UntilAsync(() => cycle.Runs.Count == 1, "the run at the next boundary");

        Assert.Equal([new BackgroundJobRun(Start.AddMinutes(68))], cycle.Runs);
        Assert.Single(SchedulerLog.Trouble(application));
    }

    [Fact]
    public async Task A_clock_that_is_set_back_keeps_an_interval_job_waiting_for_one_interval_and_no_longer()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));
        using var application = ApplicationWithJobs.On(database, clock, job);

        _ = application.Services;
        await Succeeded(database, "sync");

        // The clock is corrected by thirty days, so the run that was recorded lies in the future.
        var corrected = Start.AddDays(-30);
        clock.Set(corrected);

        // From here an hour passes with every second of the test. The job runs an interval
        // after the correction, as it would after a restart, not thirty days and an hour after it.
        await Wait.UntilAsync(
            () => job.Runs.Count == 2,
            "the run an interval after the clock was set back",
            again: () => clock.Set(clock.GetUtcNow().AddHours(1)));

        Assert.True(
            clock.GetUtcNow() < corrected.AddHours(6),
            $"The job ran only at {clock.GetUtcNow():u}, long after the clock was set back to {corrected:u}.");
        Assert.Empty(SchedulerLog.Trouble(application));
    }

    [Fact]
    public async Task A_run_that_never_finished_is_reported_and_not_repeated_before_it_is_next_due()
    {
        // What a process that died during a run leaves behind: a start, and nothing after it.
        var database = PostgresServer.CreateDatabase();
        StoredJob.Seed(database, "sync", Start);

        var clock = new TestClock(Start.AddHours(1));
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Daily));
        var later = new RecordingJob("later", BackgroundJobSchedule.Every(Daily));
        using var application = ApplicationWithJobs.On(database, clock, job, later);
        _ = application.Services;

        await Wait.UntilAsync(() => later.Runs.Count == 1, "the job that is looked at after the one under test");
        Assert.Empty(job.Runs);

        var reported = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal("Warning", reported.Level);
        Assert.Equal(SchedulerLog.Unfinished, reported.Template);
        Assert.Equal("sync", reported.Text("Job"));
        Assert.Equal(Start, reported["StartedAt"].GetDateTimeOffset());
        Assert.Equal(Start.AddHours(24), reported["NextRun"].GetDateTimeOffset());

        // A day after the start that was recorded, it is due like any other run.
        clock.Set(Start.AddHours(24));
        await Succeeded(database, "sync");

        // Nothing more is said about it. The application's own hourly job says that it skipped
        // the hours which the clock of the test jumped over.
        Assert.Equal([new BackgroundJobRun(Start.AddHours(24))], job.Runs);
        Assert.Single(SchedulerLog.Trouble(application), written => written.Text("Job") == "sync");
    }

    /// <summary>Waits until the job's latest run is in the database as succeeded.</summary>
    private static Task Succeeded(TestDatabase database, string job) =>
        Wait.UntilAsync(() => StoredJob.Of(database, job)?.Outcome == "Succeeded", $"a run of '{job}' that succeeded, in the database");
}
