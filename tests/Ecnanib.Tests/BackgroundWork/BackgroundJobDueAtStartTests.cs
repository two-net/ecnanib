using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// A job on an interval can be due when the background work starts, although its interval has
/// not passed: the scheduler asks a job that implements <see cref="IDueAtStart"/>, once each time
/// the instance takes up the work. That is how the catalog is synced at startup when it is empty
/// (FR-CAT-2). The rest of what the schedule and the state promise stands (NFR-ARC-4): a job
/// that is due anyway is not asked, a run that never finished is not repeated, and a job that
/// cannot answer does not keep the others from running.
/// </summary>
/// <remarks>
/// The scheduler goes by a clock the test sets, and it looks at its jobs in the order they are
/// registered. So when a job registered after another one has run, the scheduler has looked at
/// the other one too, at that time.
/// </remarks>
public class BackgroundJobDueAtStartTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 7, 0, TimeSpan.Zero);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    [Fact]
    public async Task A_job_that_says_it_is_due_runs_when_the_work_starts_and_its_interval_goes_on_from_that_run()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => false);

        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Succeeded(database, "sync");
        }

        // An hour later the application starts again. The interval has not passed, and the job
        // has work all the same.
        clock.Set(Start.AddHours(1));
        job.DueAtStart = () => true;
        var later = new RecordingJob("later", BackgroundJobSchedule.Every(Hourly));
        using var second = ApplicationWithJobs.On(database, clock, job, later);
        _ = second.Services;

        await Wait.UntilAsync(() => job.Runs.Count == 2, "the run as the background work starts");

        // The run is told when it became due, and is the job's latest run like any other.
        Assert.Equal(new BackgroundJobRun(Start.AddHours(1)), job.Runs[1]);
        await Succeeded(database, "sync");
        Assert.Equal(Start.AddHours(1), StoredJob.Of(database, "sync")!.StartedAt);

        // A day after the first run it is not due: the interval counts from the run of this start.
        clock.Set(Start.AddHours(24));
        await Wait.UntilAsync(() => later.Runs.Count == 2, "the job that is looked at after the one under test");
        Assert.Equal(2, job.Runs.Count);

        clock.Set(Start.AddHours(25));
        await Wait.UntilAsync(() => job.Runs.Count == 3, "the run that is due a day after the run of the start");

        Assert.Equal(Start.AddHours(25), job.Runs[2].ScheduledFor);

        // It was asked once: as the work started, and not as the job became due by itself.
        Assert.Equal(1, job.Asked);
        Assert.DoesNotContain(SchedulerLog.Trouble(second), written => written.Text("Job") == "sync");
    }

    [Fact]
    public async Task A_job_that_says_it_is_not_due_waits_for_its_interval()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => false);

        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Succeeded(database, "sync");
        }

        clock.Set(Start.AddHours(1));
        var later = new RecordingJob("later", BackgroundJobSchedule.Every(Hourly));
        using var second = ApplicationWithJobs.On(database, clock, job, later);
        _ = second.Services;

        // Asked, and not run.
        await Wait.UntilAsync(() => later.Runs.Count == 1, "the job that is looked at after the one under test");
        Assert.Equal((1, 1), (job.Asked, job.Runs.Count));

        // A day after its run, as without the question.
        clock.Set(Start.AddHours(24));
        await Wait.UntilAsync(() => job.Runs.Count == 2, "the run that is due a day after the previous one");

        Assert.Equal(Start.AddHours(24), job.Runs[1].ScheduledFor);
        Assert.Equal(1, job.Asked);
    }

    [Fact]
    public async Task A_job_that_is_due_anyway_is_not_asked()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => throw new InvalidOperationException("Nobody asks."));

        // One that has never run.
        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Succeeded(database, "sync");

            Assert.Empty(SchedulerLog.Trouble(first));
        }

        // And one whose interval passed while the application was down.
        clock.Set(Start.AddHours(30));
        using var second = ApplicationWithJobs.On(database, clock, job);
        _ = second.Services;
        await Wait.UntilAsync(() => job.Runs.Count == 2, "the run that was overdue");

        Assert.Equal(0, job.Asked);
        Assert.DoesNotContain(SchedulerLog.Trouble(second), written => written.Text("Job") == "sync");
    }

    [Fact]
    public async Task A_run_that_never_finished_is_not_repeated_by_a_job_that_would_say_it_is_due()
    {
        // What a process that died during a run leaves behind: a start, and nothing after it.
        var database = PostgresServer.CreateDatabase();
        StoredJob.Seed(database, "sync", Start);

        var clock = new TestClock(Start.AddHours(1));
        var job = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => true);
        var later = new RecordingJob("later", BackgroundJobSchedule.Every(Daily));
        using var application = ApplicationWithJobs.On(database, clock, job, later);
        _ = application.Services;

        await Wait.UntilAsync(() => later.Runs.Count == 1, "the job that is looked at after the one under test");

        // A job that brings the process down must not do so at every start, whatever it says:
        // it is not asked, and runs when it is next due, as the warning says.
        Assert.Equal((0, 0), (job.Asked, job.Runs.Count));

        var reported = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(SchedulerLog.Unfinished, reported.Template);
        Assert.Equal(Start.AddHours(24), reported["NextRun"].GetDateTimeOffset());

        clock.Set(Start.AddHours(24));
        await Wait.UntilAsync(() => job.Runs.Count == 1, "the run that is due a day after the start that was recorded");

        Assert.Equal(0, job.Asked);
    }

    [Fact]
    public async Task A_job_that_cannot_say_whether_it_is_due_is_logged_once_and_keeps_no_other_job_from_running()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var broken = new AskedJob("broken", BackgroundJobSchedule.Every(Daily), () => false);
        var sync = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => false);

        using (var first = ApplicationWithJobs.On(database, clock, broken, sync))
        {
            _ = first.Services;
            await Succeeded(database, "broken");
            await Succeeded(database, "sync");
        }

        // At the next start the first of them fails when it is asked. The one after it is due.
        clock.Set(Start.AddHours(1));
        broken.DueAtStart = () => throw new InvalidOperationException("What the job would look at is away.");
        sync.DueAtStart = () => true;
        using var second = ApplicationWithJobs.On(database, clock, broken, sync);
        _ = second.Services;

        await Wait.UntilAsync(() => sync.Runs.Count == 2, "the run of the job after the one that failed to answer");

        // Said once, as an error of the job, with when it runs instead.
        var failure = Assert.Single(SchedulerLog.Trouble(second));
        Assert.Equal(("Error", SchedulerLog.CannotSay, "broken"), (failure.Level, failure.Template, failure.Text("Job")));
        Assert.Equal(Start.AddHours(24), failure["NextRun"].GetDateTimeOffset());
        Assert.Contains("System.InvalidOperationException: What the job would look at is away.", failure.Exception);

        // It takes the job for one that waits, and the background work goes on.
        Assert.Single(broken.Runs);
        Assert.Equal(1, SchedulerLog.Count(second, SchedulerLog.Active));

        clock.Set(Start.AddHours(24));
        await Wait.UntilAsync(() => broken.Runs.Count == 2, "the run of the job when it is next due");
    }

    [Fact]
    public async Task A_job_is_asked_again_each_time_the_instance_takes_up_the_work_and_never_while_it_stands_by()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => false);

        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Succeeded(database, "sync");
        }

        // The instance that takes the lock asks its job. One that stands by does not.
        clock.Set(Start.AddHours(1));
        var idle = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => true);
        using var standingBy = ApplicationWithJobs.On(database, clock, idle);

        using (var running = ApplicationWithJobs.On(database, clock, job))
        {
            _ = running.Services;
            await Wait.UntilAsync(() => SchedulerLog.Count(running, SchedulerLog.Active) == 1, "the application taking the lock");

            _ = standingBy.Services;
            await Wait.UntilAsync(() => SchedulerLog.Count(standingBy, SchedulerLog.StandingBy) == 1, "the second instance standing by");

            Assert.Equal((1, 1), (job.Asked, job.Runs.Count));
            Assert.Equal((0, 0), (idle.Asked, idle.Runs.Count));
        }

        // The first one stopped. The other takes the work up, asks its job, and runs it.
        await Wait.UntilAsync(() => idle.Runs.Count == 1, "the run of the instance that took over");

        Assert.Equal(1, idle.Asked);
        Assert.Equal(new BackgroundJobRun(Start.AddHours(1)), idle.Runs[0]);
    }

    [Fact]
    public async Task A_job_is_asked_again_after_the_instance_lost_its_lock_and_took_it_again()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new AskedJob("sync", BackgroundJobSchedule.Every(Daily), () => false);

        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Succeeded(database, "sync");
        }

        clock.Set(Start.AddHours(1));
        using var application = ApplicationWithJobs.On(database, clock, job);
        _ = application.Services;
        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.Active) == 1, "the application taking the lock");
        Assert.Equal((1, 1), (job.Asked, job.Runs.Count));

        // The database ends the session that holds the lock. By the time the instance has the
        // lock again, the job has work.
        job.DueAtStart = () => true;
        var session = Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));
        database.Execute($"SELECT pg_terminate_backend({session})");

        await Wait.UntilAsync(() => job.Runs.Count == 2, "the run under the new lock");

        Assert.Equal(2, job.Asked);
        Assert.Equal(2, SchedulerLog.Count(application, SchedulerLog.Active));
    }

    [Fact]
    public void A_job_on_boundaries_that_would_be_asked_stops_the_application_from_starting()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = ApplicationWithJobs.On(
            database, new AskedJob("candles", BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15)), () => true));

        var refused = Assert.ThrowsAny<Exception>(() => application.Services.GetRequiredService<IServiceProvider>());

        // A run on boundaries is for its boundary, and the start of the work is none.
        Assert.StartsWith("The application did not start.", refused.Message);
        Assert.Contains("The background job 'candles' runs on UTC boundaries", refused.Message);
    }

    /// <summary>Waits until the job's latest run is in the database as succeeded.</summary>
    private static Task Succeeded(TestDatabase database, string job) =>
        Wait.UntilAsync(() => StoredJob.Of(database, job)?.Outcome == "Succeeded", $"a run of '{job}' that succeeded, in the database");
}
