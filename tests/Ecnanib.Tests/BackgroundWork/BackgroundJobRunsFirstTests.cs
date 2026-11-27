using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// On startup the application reconciles every running agent with the exchange, then resumes
/// scheduling (NFR-REL-3). The scheduler holds to that order for every job: each time the
/// instance takes up the background work, the jobs that implement <see cref="IRunsFirst"/> run
/// first, one after the other, and no other job starts until they have ended. The other jobs
/// wait for two minutes at the most. See "A job that runs first" in docs/background-work.md.
/// </summary>
/// <remarks>
/// The scheduler goes by a clock the test sets, and it looks at its jobs in the order they are
/// registered. A job that waits here is registered before the jobs that run first: left to the
/// order of the list, it would start before them.
/// </remarks>
public class BackgroundJobRunsFirstTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 7, 0, TimeSpan.Zero);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    // How long the other jobs wait for the jobs that run first: it is fixed, and no setting.
    private static readonly TimeSpan FirstRunsWithin = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task The_jobs_that_run_first_run_one_after_the_other_before_any_other_job_starts()
    {
        var database = PostgresServer.CreateDatabase();
        var did = new Sequence();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Hourly), (_, _) => Said(did, "other runs"));
        var settling = new FirstJob("settling", BackgroundJobSchedule.Every(Hourly), (_, stop) => HeldAsync(did, "settling", settled.Task, stop));
        var reconciling = new FirstJob(
            "reconciling", BackgroundJobSchedule.Every(Hourly), (_, stop) => HeldAsync(did, "reconciling", reconciled.Task, stop));

        using var application = ApplicationWithJobs.On(database, new TestClock(Start), other, settling, reconciling);
        _ = application.Services;

        // The first of them runs. Nothing else has started: not the second, and not the job
        // that is due as well and comes before both in the list. No start of either is written.
        await Wait.UntilAsync(() => settling.Runs.Count == 1, "the run of the job that runs first");

        Assert.Empty(reconciling.Runs);
        Assert.Empty(other.Runs);
        Assert.Null(StoredJob.Of(database, "reconciling"));
        Assert.Null(StoredJob.Of(database, "other"));

        // It ends, and the second one runs. The other job waits on.
        settled.SetResult();
        await Wait.UntilAsync(() => reconciling.Runs.Count == 1, "the run of the second job that runs first");

        Assert.Equal("Succeeded", StoredJob.Of(database, "settling")!.Outcome);
        Assert.Empty(other.Runs);
        Assert.Null(StoredJob.Of(database, "other"));
        Assert.Equal(0, SchedulerLog.Count(application, SchedulerLog.FirstEnded));

        // It ends as well, and only then the schedule starts.
        reconciled.SetResult();
        await Wait.UntilAsync(() => other.Runs.Count == 1, "the run of the job that waited");

        Assert.Equal(
            ["settling starts", "settling ends", "reconciling starts", "reconciling ends", "other runs"],
            did.Steps);

        // Each was a run like any other, for when the work started, and is the job's latest run.
        Assert.Equal(new BackgroundJobRun(Start), settling.Runs[0]);
        Assert.Equal(new BackgroundJobRun(Start), reconciling.Runs[0]);
        await Succeeded(database, "reconciling");

        // Said once, and none of it is trouble.
        Assert.Equal(1, SchedulerLog.Count(application, SchedulerLog.FirstEnded));
        Assert.Equal((1, 1), (settling.TakenUps, reconciling.TakenUps));
        Assert.Empty(SchedulerLog.Trouble(application));
    }

    [Fact]
    public async Task A_job_that_runs_first_runs_at_every_start_whatever_its_interval_says()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new FirstJob("reconciling", BackgroundJobSchedule.Every(Daily));

        using (var first = ApplicationWithJobs.On(database, clock, job))
        {
            _ = first.Services;
            await Succeeded(database, "reconciling");
        }

        // An hour later the application starts again. The interval has not passed, and the
        // job runs first all the same: a start reconciles, however lately it last did.
        clock.Set(Start.AddHours(1));
        var later = new RecordingJob("later", BackgroundJobSchedule.Every(Hourly));
        using var second = ApplicationWithJobs.On(database, clock, later, job);
        _ = second.Services;

        await Wait.UntilAsync(() => later.Runs.Count == 1, "the job that waited for the one that runs first");

        Assert.Equal(2, job.Runs.Count);
        Assert.Equal(new BackgroundJobRun(Start.AddHours(1)), job.Runs[1]);
        Assert.Equal(Start.AddHours(1), StoredJob.Of(database, "reconciling")!.StartedAt);

        // Its interval goes on from that run: it is not due a day after the run before it.
        clock.Set(Start.AddHours(24));
        await Wait.UntilAsync(() => later.Runs.Count == 2, "the job that is looked at before the one under test, a day later");
        Assert.Equal(2, job.Runs.Count);

        clock.Set(Start.AddHours(25));
        await Wait.UntilAsync(() => job.Runs.Count == 3, "the run that is due a day after the run of the start");

        Assert.DoesNotContain(SchedulerLog.Trouble(second), written => written.Text("Job") == "reconciling");
    }

    [Fact]
    public async Task A_job_that_runs_first_runs_also_when_its_latest_run_never_finished()
    {
        // What a process that died during a run leaves behind: a start, and nothing after it.
        var database = PostgresServer.CreateDatabase();
        StoredJob.Seed(database, "reconciling", Start);

        var clock = new TestClock(Start.AddMinutes(1));
        var job = new FirstJob("reconciling", BackgroundJobSchedule.Every(Daily));
        var later = new RecordingJob("later", BackgroundJobSchedule.Every(Daily));
        using var application = ApplicationWithJobs.On(database, clock, later, job);
        _ = application.Services;

        await Wait.UntilAsync(() => later.Runs.Count == 1, "the job that waited for the one that runs first");

        // Another job would wait for its time. This one is what a start after a process that
        // was cut off needs most, so it runs, and the warning says so.
        Assert.Single(job.Runs);
        await Succeeded(database, "reconciling");

        var reported = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(("Warning", SchedulerLog.UnfinishedFirst, "reconciling"), (reported.Level, reported.Template, reported.Text("Job")));
        Assert.Equal(Start, reported["StartedAt"].GetDateTimeOffset());
    }

    [Fact]
    public async Task A_job_that_runs_first_is_told_each_time_the_instance_takes_up_the_work_and_runs_first_again()
    {
        var database = PostgresServer.CreateDatabase();
        var did = new Sequence();
        var job = new FirstJob("reconciling", BackgroundJobSchedule.Every(Daily), (_, _) => Said(did, "runs"))
        {
            OnTakenUp = () => did.Add("told"),
        };
        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Daily), (_, _) => Said(did, "other runs"));

        // An instance that stands by is told nothing, and runs nothing.
        var idle = new FirstJob("reconciling", BackgroundJobSchedule.Every(Daily));
        using var standingBy = ApplicationWithJobs.On(database, new TestClock(Start), idle);

        using var application = ApplicationWithJobs.On(database, new TestClock(Start), other, job);
        _ = application.Services;
        await Wait.UntilAsync(() => other.Runs.Count == 1, "the job that waited for the one that runs first");

        _ = standingBy.Services;
        await Wait.UntilAsync(() => SchedulerLog.Count(standingBy, SchedulerLog.StandingBy) == 1, "the second instance standing by");

        // Told before it ran, and before anything else did.
        Assert.Equal(["told", "runs", "other runs"], did.Steps);
        Assert.Equal((0, 0), (idle.TakenUps, idle.Runs.Count));

        // The database ends the session that holds the lock. The instance takes the work up
        // again: the job is told again, and runs first again, although its day has not passed.
        var session = Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));
        database.Execute($"SELECT pg_terminate_backend({session})");

        await Wait.UntilAsync(
            () => job.Runs.Count + idle.Runs.Count == 2, "the run that comes first under the new lock, in whichever instance took it");

        Assert.Equal(2, job.TakenUps + idle.TakenUps);

        // The other job has run, and its day has not passed: it does not run again.
        Assert.Single(other.Runs);
    }

    [Fact]
    public async Task A_job_that_cannot_be_told_that_the_work_is_taken_up_runs_first_all_the_same()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new FirstJob("reconciling", BackgroundJobSchedule.Every(Daily))
        {
            OnTakenUp = () => throw new InvalidOperationException("What the job would forget is away."),
        };
        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Daily));

        using var application = ApplicationWithJobs.On(database, new TestClock(Start), other, job);
        _ = application.Services;

        await Wait.UntilAsync(() => other.Runs.Count == 1, "the job that waited for the one that runs first");

        Assert.Single(job.Runs);

        var failure = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(("Error", SchedulerLog.NotTold, "reconciling"), (failure.Level, failure.Template, failure.Text("Job")));
        Assert.Contains("System.InvalidOperationException: What the job would forget is away.", failure.Exception);
    }

    [Fact]
    public async Task A_run_that_comes_first_and_fails_keeps_no_other_job_waiting()
    {
        var database = PostgresServer.CreateDatabase();
        var failing = new FirstJob(
            "settling", BackgroundJobSchedule.Every(Daily), (_, _) => throw new InvalidOperationException("The exchange is away."));
        var after = new FirstJob("reconciling", BackgroundJobSchedule.Every(Daily));
        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Daily));

        using var application = ApplicationWithJobs.On(database, new TestClock(Start), other, failing, after);
        _ = application.Services;

        await Wait.UntilAsync(() => other.Runs.Count == 1, "the job that waited for those that run first");

        // The one after it ran, and then the schedule started. The failure is the job's own:
        // said once, as every failed run is, and it runs again when it is next due.
        Assert.Equal((1, 1), (failing.Runs.Count, after.Runs.Count));
        Assert.Equal("Failed", StoredJob.Of(database, "settling")!.Outcome);

        var failure = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(("Error", SchedulerLog.Failed, "settling"), (failure.Level, failure.Template, failure.Text("Job")));
        Assert.Equal(1, SchedulerLog.Count(application, SchedulerLog.FirstEnded));
    }

    [Fact]
    public async Task The_other_jobs_start_beside_a_run_that_comes_first_and_has_not_ended_within_two_minutes()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var slow = new FirstJob("settling", BackgroundJobSchedule.Every(Hourly), RecordingJob.UntilStopped);
        var behind = new FirstJob("reconciling", BackgroundJobSchedule.Every(Hourly));
        var other = new RecordingJob("other", BackgroundJobSchedule.Every(Hourly));

        using var application = ApplicationWithJobs.On(database, clock, other, slow, behind);
        _ = application.Services;
        await Wait.UntilAsync(() => slow.Runs.Count == 1, "the run of the job that runs first");

        // An exchange that does not answer: the run goes on. A moment before the two minutes
        // are over, everything else still waits.
        clock.Set(Start + FirstRunsWithin - TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => StoredJob.Of(database, "settling") is { FinishedAt: null }, "the run that goes on");

        Assert.Empty(other.Runs);
        Assert.Empty(behind.Runs);

        // Two minutes after the work was taken up, the other jobs start beside it: no backup
        // and no alert waits for longer. The job that was to run after it runs now as well.
        clock.Set(Start + FirstRunsWithin);
        await Wait.UntilAsync(() => other.Runs.Count == 1 && behind.Runs.Count == 1, "the jobs that waited");

        Assert.Equal(0, slow.Ended);
        Assert.Equal(new BackgroundJobRun(Start), other.Runs[0]);

        // Said once, as a warning, with the jobs that had not ended.
        var late = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(("Warning", SchedulerLog.FirstLate), (late.Level, late.Template));
        Assert.Equal("settling, reconciling", late.Text("Jobs"));
        Assert.Equal(2, late["Minutes"].GetDouble());
        Assert.Equal(0, SchedulerLog.Count(application, SchedulerLog.FirstEnded));
    }

    [Fact]
    public async Task A_job_that_can_be_due_as_the_work_starts_is_asked_once_the_jobs_that_run_first_have_ended()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var asked = new AskedJob("results", BackgroundJobSchedule.Every(Daily), () => false);
        var first = new FirstJob("settling", BackgroundJobSchedule.Every(Daily));

        using (var before = ApplicationWithJobs.On(database, clock, asked, first))
        {
            _ = before.Services;
            await Succeeded(database, "results");
        }

        // At the next start the job that runs first leaves work for the other one, which
        // answers from what it finds then.
        clock.Set(Start.AddHours(1));
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Work = (_, stop) => settled.Task.WaitAsync(stop);
        asked.DueAtStart = () => settled.Task.IsCompleted;

        using var application = ApplicationWithJobs.On(database, clock, asked, first);
        _ = application.Services;
        await Wait.UntilAsync(() => first.Runs.Count == 2, "the run of the job that runs first");

        // Not asked while that run goes on: it would have said that it has no work.
        Assert.Equal(0, asked.Asked);

        settled.SetResult();
        await Wait.UntilAsync(() => asked.Runs.Count == 2, "the run of the job that was left work");

        Assert.Equal(1, asked.Asked);
        Assert.Equal(new BackgroundJobRun(Start.AddHours(1)), asked.Runs[1]);
    }

    [Fact]
    public void A_job_on_boundaries_that_would_run_first_stops_the_application_from_starting()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = ApplicationWithJobs.On(
            database, new FirstJob("candles", BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15))));

        var refused = Assert.ThrowsAny<Exception>(() => application.Services.GetRequiredService<IServiceProvider>());

        // A run on boundaries is for its boundary, and the start of the work is none.
        Assert.StartsWith("The application did not start.", refused.Message);
        Assert.Contains("The background job 'candles' runs on UTC boundaries, so it cannot run first", refused.Message);
    }

    private static Task Said(Sequence did, string step)
    {
        did.Add(step);

        return Task.CompletedTask;
    }

    /// <summary>A run that says when it starts and ends, and goes on until the test lets it end.</summary>
    private static async Task HeldAsync(Sequence did, string name, Task until, CancellationToken stop)
    {
        did.Add($"{name} starts");

        await until.WaitAsync(stop);

        did.Add($"{name} ends");
    }

    /// <summary>Waits until the job's latest run is in the database as succeeded.</summary>
    private static Task Succeeded(TestDatabase database, string job) =>
        Wait.UntilAsync(() => StoredJob.Of(database, job)?.Outcome == "Succeeded", $"a run of '{job}' that succeeded, in the database");
}
