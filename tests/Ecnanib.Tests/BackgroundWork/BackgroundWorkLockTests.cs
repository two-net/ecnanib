using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// The scheduler must not run twice, so it takes a database lock: starting a second instance
/// does not run the background work a second time (NFR-ARC-4). Version 1 targets a single
/// running instance, and the lock is what makes a second one safe.
/// </summary>
public class BackgroundWorkLockTests
{
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    [Fact]
    public async Task The_running_application_holds_the_background_work_lock_and_not_the_migration_lock()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = ApplicationWithJobs.On(database);

        _ = application.Services;
        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.Active) == 1, "the application taking the lock");

        Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));
        Assert.Empty(database.SessionsHolding(DatabaseMigration.LockKey));
        Assert.NotEqual(DatabaseMigration.LockKey, BackgroundJobs.LockKey);

        // It is taken before any job is due. The application has one job of its own, which
        // checks the stored keys on the hour and so has nothing to do at this start.
        var active = Assert.Single(SchedulerLog.Of(application), written => written.Template == SchedulerLog.Active);
        Assert.Equal(1, active["JobCount"].GetInt32());
    }

    [Fact]
    public async Task A_second_instance_stands_by_and_runs_nothing()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly));
        using var application = ApplicationWithJobs.On(database, job);

        // Another instance, as far as the database can tell: a session that holds the lock.
        using var other = database.Open();
        Lock(other, "pg_advisory_lock");

        _ = application.Services;
        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.StandingBy) == 1, "the application standing by");

        // It serves requests all the same, and the job, which is due, does not run.
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        Assert.True(health.IsSuccessStatusCode);
        Assert.Empty(job.Runs);
        Assert.Null(StoredJob.Of(database, "sync"));
        Assert.Equal(0, SchedulerLog.Count(application, SchedulerLog.Active));

        // The other instance stops, and this one takes over.
        Lock(other, "pg_advisory_unlock");
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Succeeded", "the run after taking over");

        Assert.Single(job.Runs);
        Assert.Equal(1, SchedulerLog.Count(application, SchedulerLog.StandingBy));
        Assert.Equal(1, SchedulerLog.Count(application, SchedulerLog.Active));
        Assert.Empty(SchedulerLog.Trouble(application));
    }

    [Fact]
    public async Task Two_instances_that_start_together_run_a_due_job_once_and_one_takes_over_from_the_other()
    {
        var database = PostgresServer.CreateDatabase();

        // One job in both instances, and a job of its own for each, looked at after it.
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly));
        var ofOne = new RecordingJob("of-one", BackgroundJobSchedule.Every(Hourly));
        var ofTwo = new RecordingJob("of-two", BackgroundJobSchedule.Every(Hourly));
        var one = ApplicationWithJobs.On(database, job, ofOne);
        var two = ApplicationWithJobs.On(database, job, ofTwo);

        var running = new List<LoggedApplication> { one, two };
        try
        {
            await Task.WhenAll(
                    Task.Run(() => one.Services, TestContext.Current.CancellationToken),
                    Task.Run(() => two.Services, TestContext.Current.CancellationToken))
                .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            await Wait.UntilAsync(
                () => Runs(one) + Runs(two) == 1 && StandsBy(one) + StandsBy(two) == 1,
                "one instance running the background work and the other standing by");
            await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Succeeded", "the run of the job that is due");

            // One of them runs the work, and it ran the job that both have, once.
            var (holder, standby, ofStandby) = Runs(one) == 1 ? (one, two, ofTwo) : (two, one, ofOne);
            Assert.Equal(0, Runs(standby));
            Assert.Single(job.Runs);
            Assert.Empty(ofStandby.Runs);
            Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));

            // It stops, and the other one takes over. Its own job has never run, so it runs now:
            // by then it has looked at the job that both have, which is not due again.
            running.Remove(holder);
            holder.Dispose();

            await Wait.UntilAsync(() => ofStandby.Runs.Count == 1, "the instance that stood by running its own job");

            Assert.Equal(1, Runs(standby));
            Assert.Single(job.Runs);
            Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));
            Assert.Empty(SchedulerLog.Trouble(standby));
        }
        finally
        {
            running.ForEach(application => application.Dispose());
        }

        static int Runs(LoggedApplication application) => SchedulerLog.Count(application, SchedulerLog.Active);
        static int StandsBy(LoggedApplication application) => SchedulerLog.Count(application, SchedulerLog.StandingBy);
    }

    [Fact]
    public async Task An_instance_that_loses_its_lock_stops_its_jobs_and_takes_the_lock_again()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly), RecordingJob.UntilStopped);
        using var application = ApplicationWithJobs.On(database, job);

        _ = application.Services;
        await Wait.UntilAsync(() => job.Runs.Count == 1, "the run that losing the lock cuts short");
        var session = Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));

        // The database ends the session that holds the lock, and with it the lock.
        database.Execute($"SELECT pg_terminate_backend({session})");

        await Wait.UntilAsync(() => job.Stopped == 1, "the job being stopped");
        await Wait.UntilAsync(() => SchedulerLog.Count(application, SchedulerLog.Active) == 2, "the application taking the lock again");

        // Said once, as a warning. The application has run on.
        var lost = Assert.Single(SchedulerLog.Trouble(application));
        Assert.Equal("Warning", lost.Level);
        Assert.Equal(SchedulerLog.SessionEnded, lost.Template);
        Assert.NotNull(lost.Exception);

        var again = Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));
        Assert.NotEqual(session, again);

        // The run that was stopped left its work to do, so the job runs again under the new lock.
        await Wait.UntilAsync(() => job.Runs.Count == 2, "the job running again");
    }

    [Fact]
    public async Task Stopping_the_application_stops_a_job_in_flight_and_releases_the_lock()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly), RecordingJob.UntilStopped);
        using var application = ApplicationWithJobs.On(database, job);

        _ = application.Services;
        await Wait.UntilAsync(() => job.Runs.Count == 1, "the run that the stop cuts short");
        Assert.Single(database.SessionsHolding(BackgroundJobs.LockKey));

        await application.Services.GetRequiredService<IHost>().StopAsync(TestContext.Current.CancellationToken);

        // By the time the application has stopped, the run has ended and is recorded.
        Assert.Equal(1, job.Stopped);
        Assert.Equal("Stopped", StoredJob.Of(database, "sync")!.Outcome);

        // The lock goes with its session, which the database ends a moment after it is closed.
        await Wait.UntilAsync(() => database.SessionsHolding(BackgroundJobs.LockKey).Count == 0, "the lock being released");

        // None of this is trouble.
        Assert.Empty(SchedulerLog.Trouble(application));
        Assert.Equal(1, SchedulerLog.Count(application, SchedulerLog.StoppedRun));
    }

    private static void Lock(NpgsqlConnection session, string function)
    {
        using var command = new NpgsqlCommand($"SELECT {function}(@key)", session);
        command.Parameters.AddWithValue("key", BackgroundJobs.LockKey);
        command.ExecuteScalar();
    }
}
