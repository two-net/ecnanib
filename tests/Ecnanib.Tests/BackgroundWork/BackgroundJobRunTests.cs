using System.Data.Common;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// How the platform runs a job: a failure is logged once and is not retried in a loop, jobs do
/// not wait for each other, a job never runs twice at the same time, and nothing that goes wrong
/// with the background work stops the application (NFR-ARC-4, NFR-LOG-6).
/// </summary>
public class BackgroundJobRunTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 7, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_job_that_fails_is_logged_once_and_runs_again_only_when_it_is_next_due()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new RecordingJob(
            "sync",
            BackgroundJobSchedule.Every(TimeSpan.FromHours(1)),
            (_, _) => throw new InvalidOperationException("The provider did not answer."));
        var often = new RecordingJob("often", BackgroundJobSchedule.Every(TimeSpan.FromMinutes(30)));
        using var application = ApplicationWithJobs.On(database, clock, job, often);

        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Failed", "the failed run in the database");

        var failure = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal("Error", failure.Level);
        Assert.Equal(SchedulerLog.Failed, failure.Template);
        Assert.Equal("sync", failure.Text("Job"));
        Assert.True(failure.Has("DurationMs"));
        Assert.Contains("System.InvalidOperationException: The provider did not answer.", failure.Exception);
        Assert.Contains("   at ", failure.Exception);

        // The application runs on, and so does the other job.
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        Assert.True(health.IsSuccessStatusCode);

        // Half an hour on, the scheduler has looked at the failed job again and left it alone.
        clock.Set(Start.AddMinutes(30));
        await Wait.UntilAsync(() => often.Runs.Count == 2, "the other job half an hour later");
        Assert.Single(job.Runs);

        // It is tried again when it is due, and fails again.
        clock.Set(Start.AddHours(1));
        await Wait.UntilAsync(() => SchedulerLog.Trouble(application).Count == 2, "the second failure");

        Assert.Equal([Start, Start.AddHours(1)], job.Runs.Select(run => run.ScheduledFor));
        Assert.All(SchedulerLog.Trouble(application), written => Assert.Equal(SchedulerLog.Failed, written.Template));
    }

    [Fact]
    public async Task Jobs_run_side_by_side_and_a_job_never_overlaps_itself()
    {
        var database = PostgresServer.CreateDatabase();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new RecordingJob(
            "slow",
            BackgroundJobSchedule.Every(TimeSpan.FromMilliseconds(200)),
            (_, stopped) => release.Task.WaitAsync(stopped));
        var quick = new RecordingJob("quick", BackgroundJobSchedule.Every(TimeSpan.FromMilliseconds(200)));
        using var application = ApplicationWithJobs.On(database, slow, quick);

        _ = application.Services;

        // The quick job comes round again and again while the slow one is still on its first run.
        await Wait.UntilAsync(() => quick.Runs.Count >= 4, "the quick job running while the slow one is busy");
        Assert.Single(slow.Runs);

        // Its next run is due all that time, which is said once.
        var held = Assert.Single(SchedulerLog.Trouble(application), written => written.Text("Job") == "slow");
        Assert.Equal("Warning", held.Level);
        Assert.Equal(SchedulerLog.Waits, held.Template);

        // When the run ends, the one that was waiting starts.
        release.SetResult();
        await Wait.UntilAsync(() => slow.Runs.Count >= 2, "the slow job running again");
    }

    [Fact]
    public async Task A_boundary_that_falls_inside_a_run_is_skipped()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cycle = new RecordingJob(
            "cycle",
            BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15)),
            (_, stopped) => release.Task.WaitAsync(stopped));
        using var application = ApplicationWithJobs.On(database, clock, cycle);

        _ = application.Services;
        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.Active) == 1, "the application running the background work");

        clock.Set(At(12, 15));
        await Wait.UntilAsync(() => cycle.Runs.Count == 1, "the run at 12:15");

        // Still running at 12:30.
        clock.Set(At(12, 30));
        await Wait.UntilAsync(() => SchedulerLog.Trouble(application).Count == 1, "the skipped boundary being reported");
        var skipped = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal(SchedulerLog.Skipped, skipped.Template);
        Assert.Equal(At(12, 30), skipped["DueAt"].GetDateTimeOffset());

        // The run ends at 12:31. Nothing is made up for: the next run is the one at 12:45.
        clock.Set(At(12, 31));
        release.SetResult();
        await Wait.UntilAsync(() => StoredJob.Of(database, "cycle")?.Outcome == "Succeeded", "the end of the run");

        clock.Set(At(12, 45));
        await Wait.UntilAsync(() => cycle.Runs.Count == 2, "the run at 12:45");

        Assert.Equal([At(12, 15), At(12, 45)], cycle.Runs.Select(run => run.ScheduledFor));

        static DateTimeOffset At(int hour, int minute) => new(2026, 11, 11, hour, minute, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task A_database_that_goes_away_does_not_stop_the_application()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));
        using var application = ApplicationWithJobs.On(database, job);

        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Succeeded", "the first run");

        try
        {
            // The database takes no connections and ends the ones it has.
            Refuse(database, connections: true);

            await Wait.UntilAsync(() => SchedulerLog.Trouble(application).Count >= 1, "the application noticing that its lock is gone");

            using var client = application.CreateClient();
            using var health = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
            Assert.True(health.IsSuccessStatusCode);
        }
        finally
        {
            Refuse(database, connections: false);
        }

        // It is back, and the application takes up the background work again by itself.
        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.Active) == 2, "the application taking the lock again");

        // Said once, for as long as it lasted.
        var away = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal("Warning", away.Level);
        Assert.Equal(SchedulerLog.SessionEnded, away.Template);

        // The job ran before, so it is not due again: the state outlived the outage.
        Assert.Single(job.Runs);
        Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));
    }

    [Fact]
    public async Task A_run_that_was_stopped_because_the_database_went_away_runs_again_when_it_is_back()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)), RecordingJob.UntilStopped);
        using var application = ApplicationWithJobs.On(database, job);

        _ = application.Services;
        await Wait.UntilAsync(() => job.Runs.Count == 1, "the run that is going when the database goes away");

        try
        {
            // The lock goes with the database, so the run is stopped. That it was stopped
            // cannot be written anywhere for now.
            Refuse(database, connections: true);
            await Wait.UntilAsync(() => job.Stopped == 1, "the run being stopped");
        }
        finally
        {
            job.Work = null;
            Refuse(database, connections: false);
        }

        // It is written when the database is back, so the job is known to have work left and
        // runs at once, not an hour after the run that was stopped.
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Succeeded", "the run after the database is back");

        Assert.Equal(2, job.Runs.Count);
        Assert.DoesNotContain(SchedulerLog.Trouble(application), written => written.Template == SchedulerLog.Unfinished);
        Assert.Single(SchedulerLog.Trouble(application), written => written.Template == SchedulerLog.SessionEnded);
    }

    [Fact]
    public async Task A_start_that_the_database_took_but_did_not_confirm_is_still_run()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services => services
                .AddSingleton<IBackgroundJob>(job)
                .ConfigureDbContext<EcnanibDbContext>(options => options.AddInterceptors(new LosesTheAnswer()))),
            database);

        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Succeeded", "the run of the job");

        // The first write of the start failed after it was done. The next try finds the start
        // in the database, and it is this instance's own: the job runs, once.
        Assert.Single(job.Runs);
        var failed = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal("Warning", failed.Level);
        Assert.Equal(SchedulerLog.NotStarted, failed.Template);
        Assert.Equal(0, SchedulerLog.Count(application, SchedulerLog.StartedElsewhere));
    }

    [Fact]
    public async Task Trouble_that_ends_and_comes_back_is_reported_again()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));
        using var application = ApplicationWithJobs.On(database, job);

        // Another instance holds the lock throughout, so this one only ever asks for it.
        using var other = database.Open();
        using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", other))
        {
            take.Parameters.AddWithValue("key", BackgroundJobs.LockKey);
            take.ExecuteScalar();
        }

        _ = application.Services;

        for (var time = 1; time <= 2; time++)
        {
            await Wait.UntilAsync(
                () => SchedulerLog.Count(application, SchedulerLog.StandingBy) == time,
                $"the application standing by, time {time}");

            // The session it asks on is ended. It reports that, and asks on a new one.
            EndTheSessionThatAsksForTheLock(database);

            await Wait.UntilAsync(
                () => SchedulerLog.Trouble(application).Count == time,
                $"the application reporting that it cannot ask, time {time}");
        }

        Assert.All(SchedulerLog.Trouble(application), written =>
        {
            Assert.Equal("Error", written.Level);
            Assert.Equal(SchedulerLog.CannotGoOn, written.Template);
        });
        Assert.Empty(job.Runs);
    }

    [Fact]
    public async Task An_instance_that_stands_by_reports_a_database_that_is_away_once_and_goes_on_trying()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(1)));
        using var application = ApplicationWithJobs.On(database, job);

        // Another instance holds the lock, so this one only ever asks for it.
        using var other = database.Open();
        using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", other))
        {
            take.Parameters.AddWithValue("key", BackgroundJobs.LockKey);
            take.ExecuteScalar();
        }

        _ = application.Services;
        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.StandingBy) == 1, "the application standing by");

        try
        {
            // This ends the other instance's session too, and its lock with it.
            Refuse(database, connections: true);

            await Wait.UntilAsync(() => SchedulerLog.Trouble(application).Count >= 1, "the application failing to ask for the lock");
        }
        finally
        {
            Refuse(database, connections: false);
        }

        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Succeeded", "the run after the database is back");

        var away = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal("Error", away.Level);
        Assert.Equal(SchedulerLog.CannotGoOn, away.Template);
        Assert.NotNull(away.Exception);
        Assert.Equal(1, SchedulerLog.Count(application, SchedulerLog.Active));
        Assert.Single(job.Runs);
    }

    [Fact]
    public void Two_jobs_with_one_name_stop_the_application_from_starting()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = ApplicationWithJobs.On(
            database,
            new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(1))),
            new RecordingJob("sync", BackgroundJobSchedule.Every(TimeSpan.FromHours(2))));

        var refused = Assert.ThrowsAny<Exception>(() => application.Services.GetRequiredService<IServiceProvider>());

        // The name is the key of a job's state, so two jobs cannot share one.
        Assert.StartsWith("The application did not start.", refused.Message);
        Assert.Contains("More than one background job is named 'sync'", refused.Message);
    }

    [Fact]
    public async Task A_component_registers_its_job_with_AddBackgroundJob()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),

            // As a component does in its Add<Name>(). Twice is once.
            host => host.ConfigureServices(services => services
                .AddBackgroundJob<RegisteredJob>()
                .AddBackgroundJob<RegisteredJob>()),
            database);

        // Beside the jobs that the components of the application registered themselves.
        var job = Assert.Single(application.Services.GetServices<IBackgroundJob>().OfType<RegisteredJob>());
        await Wait.UntilAsync(() => StoredJob.Of(database, job.Name)?.Outcome == "Succeeded", "the run of the registered job");

        Assert.Equal(1, job.Runs);
        var active = Assert.Single(SchedulerLog.Of(application), written => written.Template == SchedulerLog.Active);
        Assert.Equal(application.Services.GetServices<IBackgroundJob>().Count(), active["JobCount"].GetInt32());
        Assert.Contains(application.Services.GetServices<IBackgroundJob>(), registered => registered.Name == "key-validation");
    }

    /// <summary>
    /// Makes the database refuse every connection and end the ones it has, or take them again.
    /// It is told so from another database of the same server.
    /// </summary>
    private static void Refuse(TestDatabase database, bool connections)
    {
        var server = PostgresServer.SharedDatabase;
        server.Execute($"""ALTER DATABASE "{database.Name}" ALLOW_CONNECTIONS {(connections ? "false" : "true")}""");

        if (connections)
        {
            server.Execute($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database.Name}'");
        }
    }

    private static void EndTheSessionThatAsksForTheLock(TestDatabase database)
    {
        var session = Assert.Single(database.Column(
            """
            SELECT pid::text FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid() AND query LIKE '%pg_try_advisory_lock%'
            """));

        database.Execute($"SELECT pg_terminate_backend({session})");
    }

    /// <summary>
    /// Fails the first write of a job's first start after the database has done it, as a
    /// connection does that drops before the answer arrives.
    /// </summary>
    private sealed class LosesTheAnswer : DbCommandInterceptor
    {
        private int _lost;

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO background_jobs", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _lost, 1) == 0)
            {
                throw new InvalidOperationException("The connection was lost before the answer arrived.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RegisteredJob : IBackgroundJob
    {
        private int _runs;

        public string Name => "registered";

        public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(TimeSpan.FromHours(1));

        public int Runs => Volatile.Read(ref _runs);

        public Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);

            return Task.CompletedTask;
        }
    }
}
