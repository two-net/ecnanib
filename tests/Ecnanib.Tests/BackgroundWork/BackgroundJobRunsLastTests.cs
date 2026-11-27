using System.Diagnostics;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// Shutdown is graceful: in-flight cycles finish or abort safely, and no entry is left without
/// protection (NFR-REL-5). The scheduler holds to an order for it: when the application stops,
/// every run is told to stop and waited for, and then the jobs that implement
/// <see cref="IRunsLast"/> do what must not be left undone, under the lock, before it is let go.
/// The work stops as the application begins to stop, and what is done last ends before the host
/// waits no longer. See "A job that runs last" in docs/background-work.md.
/// </summary>
public class BackgroundJobRunsLastTests
{
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    // How long before the host waits no longer nothing more is begun of what is done last: it
    // is fixed, and no setting.
    private static readonly TimeSpan EndsBefore = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Stopping_the_application_does_what_comes_last_once_every_run_has_ended_and_before_the_lock_is_let_go()
    {
        var database = PostgresServer.CreateDatabase();
        var did = new Sequence();
        var locks = -1;

        // A run that takes a moment to end once it is told to stop, as a cycle does whose entry
        // is on its way.
        var cycles = new RecordingJob("cycles", BackgroundJobSchedule.Every(Hourly), async (_, stop) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stop);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
                did.Add("the run has ended");

                throw;
            }
        });
        var settling = new LastJob("settling", BackgroundJobSchedule.Every(Hourly), last: _ =>
        {
            locks = database.SessionsHolding(BackgroundJobs.LockKey).Count;
            did.Add("what comes last is done");

            return Task.CompletedTask;
        });

        using var application = ApplicationWithJobs.On(database, cycles, settling);
        _ = application.Services;
        await Wait.UntilAsync(() => cycles.Runs.Count == 1 && StoredJob.Of(database, "settling")?.Outcome == "Succeeded", "the runs of the start");

        // While the application runs, nothing is done last.
        Assert.Equal(0, settling.LastRuns);

        await application.Services.GetRequiredService<IHost>().StopAsync(TestContext.Current.CancellationToken);

        // By the time the application has stopped: the run was waited for, and then what
        // comes last was done, once, by the instance that still held the lock.
        Assert.Equal(["the run has ended", "what comes last is done"], did.Steps);
        Assert.Equal(1, settling.LastRuns);
        Assert.Equal(1, locks);
        Assert.Equal("Stopped", StoredJob.Of(database, "cycles")!.Outcome);

        // It is no run of the job: its row is about the run of the start still.
        Assert.Single(settling.Runs);
        Assert.Equal("Succeeded", StoredJob.Of(database, "settling")!.Outcome);

        await Wait.UntilAsync(() => database.SessionsHolding(BackgroundJobs.LockKey).Count == 0, "the lock being released");

        // Said once, and none of it is trouble.
        var said = Assert.Single(SchedulerLog.Of(application), written => written.Template == SchedulerLog.DidLast);
        Assert.Equal(("Information", "settling"), (said.Level, said.Text("Job")));
        Assert.Empty(SchedulerLog.Trouble(application));
    }

    [Fact]
    public async Task The_work_stops_as_the_application_begins_to_stop_and_not_when_the_host_gets_to_the_scheduler()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("cycles", BackgroundJobSchedule.Every(Hourly), RecordingJob.UntilStopped);
        var settling = new LastJob("settling", BackgroundJobSchedule.Every(Hourly));

        using var application = ApplicationWithJobs.On(database, job, settling);
        _ = application.Services;
        await Wait.UntilAsync(() => job.Runs.Count == 1, "the run that the stop cuts short");

        // The application is told to stop, and nothing more: no service of the host is stopped
        // yet. The host stops its services one after the other, the web server before this
        // one, and the time that the application has to stop in counts from now.
        application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await Wait.UntilAsync(() => settling.LastRuns == 1, "what comes last, as the application begins to stop");

        Assert.Equal(1, job.Stopped);
        Assert.Equal("Stopped", StoredJob.Of(database, "cycles")!.Outcome);
        await Wait.UntilAsync(() => database.SessionsHolding(BackgroundJobs.LockKey).Count == 0, "the lock being released");
        Assert.Empty(SchedulerLog.Trouble(application));
    }

    [Fact]
    public async Task Nothing_is_done_last_when_the_lock_is_lost_and_nothing_by_an_instance_that_stands_by()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new LastJob("settling", BackgroundJobSchedule.Every(Hourly), work: RecordingJob.UntilStopped);
        var idle = new LastJob("settling", BackgroundJobSchedule.Every(Hourly));

        using var application = ApplicationWithJobs.On(database, job);
        _ = application.Services;
        await Wait.UntilAsync(() => job.Runs.Count == 1, "the run that losing the lock cuts short");

        using (var standingBy = ApplicationWithJobs.On(database, idle))
        {
            _ = standingBy.Services;
            await Wait.UntilAsync(() => SchedulerLog.Count(standingBy, SchedulerLog.StandingBy) == 1, "the second instance standing by");
        }

        // An instance that stood by has stopped. It did not run the work, so it did nothing last.
        Assert.Equal(0, idle.LastRuns);

        // The database ends the session that holds the lock, and with it the lock. The run is
        // stopped, and nothing is done last: the application runs on, and whichever instance
        // has the lock goes on with the work.
        var session = Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));
        database.Execute($"SELECT pg_terminate_backend({session})");

        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.Active) == 2, "the application taking the lock again");

        Assert.Equal(0, job.LastRuns);

        // It is done when the application stops.
        await application.Services.GetRequiredService<IHost>().StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, job.LastRuns);
    }

    [Fact]
    public async Task An_error_in_what_comes_last_is_logged_once_and_the_application_stops_all_the_same()
    {
        var database = PostgresServer.CreateDatabase();
        var failing = new LastJob(
            "settling", BackgroundJobSchedule.Every(Hourly), last: _ => throw new InvalidOperationException("The exchange is away."));
        var after = new LastJob("closing", BackgroundJobSchedule.Every(Hourly));

        using var application = ApplicationWithJobs.On(database, failing, after);
        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "closing")?.Outcome == "Succeeded", "the runs of the start");

        await application.Services.GetRequiredService<IHost>().StopAsync(TestContext.Current.CancellationToken);

        // The one after it did what it does last, and the lock is let go.
        Assert.Equal((1, 1), (failing.LastRuns, after.LastRuns));
        await Wait.UntilAsync(() => database.SessionsHolding(BackgroundJobs.LockKey).Count == 0, "the lock being released");

        var failure = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(("Error", SchedulerLog.LastFailed, "settling"), (failure.Level, failure.Template, failure.Text("Job")));
        Assert.Contains("System.InvalidOperationException: The exchange is away.", failure.Exception);
    }

    [Fact]
    public async Task What_comes_last_is_told_when_the_time_to_stop_in_is_nearly_over_and_the_application_stops_in_time()
    {
        var database = PostgresServer.CreateDatabase();

        // An exchange that does not answer: what is done last goes on until it is told to end.
        var job = new LastJob("settling", BackgroundJobSchedule.Every(Hourly), last: stop => Task.Delay(Timeout.InfiniteTimeSpan, stop));

        // The host waits for two seconds longer than what is kept for the calls that are under
        // way when the time is nearly over.
        using var application = ApplicationWithJobs.On(database, clock: null, stopsWithin: EndsBefore + TimeSpan.FromSeconds(2), job);
        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "settling")?.Outcome == "Succeeded", "the run of the start");

        var stopping = Stopwatch.StartNew();
        await application.Services.GetRequiredService<IHost>().StopAsync(TestContext.Current.CancellationToken);

        // Told after those two seconds, and long before the host would have waited no longer.
        Assert.Equal(1, job.LastRuns);
        Assert.InRange(stopping.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        var late = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(("Warning", SchedulerLog.LastNotEnded, "settling"), (late.Level, late.Template, late.Text("Job")));
        await Wait.UntilAsync(() => database.SessionsHolding(BackgroundJobs.LockKey).Count == 0, "the lock being released");
    }

    [Fact]
    public async Task Nothing_is_done_last_when_the_runs_have_not_ended_before_the_time_to_stop_in_is_nearly_over()
    {
        var database = PostgresServer.CreateDatabase();
        var ended = false;

        // A run that does not end when it is told to, for three seconds.
        var slow = new RecordingJob("cycles", BackgroundJobSchedule.Every(Hourly), async (_, stop) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stop);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
                ended = true;

                throw;
            }
        });
        var settling = new LastJob("settling", BackgroundJobSchedule.Every(Hourly));

        // One second is left for what comes last, and the run takes three to end.
        using var application = ApplicationWithJobs.On(database, clock: null, stopsWithin: EndsBefore + TimeSpan.FromSeconds(1), slow, settling);
        _ = application.Services;
        await Wait.UntilAsync(() => slow.Runs.Count == 1 && StoredJob.Of(database, "settling")?.Outcome == "Succeeded", "the runs of the start");

        await application.Services.GetRequiredService<IHost>().StopAsync(TestContext.Current.CancellationToken);

        // The run was waited for all the same, before the lock was let go: it never outlives
        // the lock that it began under. What comes last was not begun so late.
        Assert.True(ended);
        Assert.Equal("Stopped", StoredJob.Of(database, "cycles")!.Outcome);
        Assert.Equal(0, settling.LastRuns);

        var skipped = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(("Warning", SchedulerLog.NothingLast), (skipped.Level, skipped.Template));
        Assert.Equal(("settling", "cycles"), (skipped.Text("Jobs"), skipped.Text("Running")));
    }

    [Fact]
    public async Task An_application_whose_work_has_not_ended_when_the_host_waits_no_longer_says_so_once()
    {
        var database = PostgresServer.CreateDatabase();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A run that does not end when it is told to, until the test lets it.
        var stuck = new RecordingJob("cycles", BackgroundJobSchedule.Every(Hourly), async (_, stop) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stop);
            }
            catch (OperationCanceledException)
            {
                await ended.Task;

                throw;
            }
        });

        using var application = ApplicationWithJobs.On(database, clock: null, stopsWithin: TimeSpan.FromSeconds(1), stuck);
        _ = application.Services;
        await Wait.UntilAsync(() => stuck.Runs.Count == 1, "the run that does not end");

        var host = application.Services.GetRequiredService<IHost>();

        // The host waits for a second, and then no longer. Stopped a second time, as what
        // disposes of an application does, it says nothing more.
        await host.StopAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var unended = Assert.Single(SchedulerLog.Of(application), written => written.Level == "Fatal");
        Assert.Equal((SchedulerLog.Unended, "cycles"), (unended.Template, unended.Text("Jobs")));

        // The process ends with the run under way. Its row says a start with no end, which
        // the next start finds: see BackgroundJobStateTests.
        Assert.Null(StoredJob.Of(database, "cycles")!.FinishedAt);

        ended.SetResult();
        await Wait.UntilAsync(() => stuck.Stopped == 1, "the run ending, once the test lets it");
    }
}
