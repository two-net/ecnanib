using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// The health of the scheduler, as an operator status screen shows it (FR-OPS-2): each job with
/// its schedule and its latest run, and whether a run of it is overdue. It is read from the
/// database, where the scheduler keeps the latest run of each job (NFR-ARC-4).
/// </summary>
/// <remarks>
/// Most of these tests hold the background work lock themselves, as another instance would. The
/// application then stands by and runs no job, so a row says what the test wrote, and the answer
/// is that of an instance which does not run the work: it is the same.
/// </remarks>
public class BackgroundWorkStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 7, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    [Fact]
    public async Task A_job_says_its_schedule_and_how_its_latest_run_ended()
    {
        var database = PostgresServer.CreateDatabase();
        var clock = new TestClock(Start);
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly));
        using var application = ApplicationWithJobs.On(database, clock, job);

        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Succeeded", "the first run of the job");

        var status = await OfAsync(application, "sync");

        Assert.Equal(BackgroundJobRunState.Succeeded, status.State);
        Assert.Equal(Start, status.LastStartedAt);
        Assert.Equal(Start, status.LastFinishedAt);

        // An interval is measured from the start of the run before, and that is when it is due.
        Assert.Same(job.Schedule, status.Schedule);
        Assert.Equal(Hourly, status.Schedule!.Period);
        Assert.False(status.Schedule.OnUtcBoundaries);
        Assert.Equal(Start + Hourly, status.DueAt);
        Assert.False(status.Overdue);
    }

    [Fact]
    public async Task A_run_that_failed_says_so()
    {
        var database = PostgresServer.CreateDatabase();
        var job = new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly), (_, _) => throw new InvalidOperationException("Planted."));
        using var application = ApplicationWithJobs.On(database, new TestClock(Start), job);

        _ = application.Services;
        await Wait.UntilAsync(() => StoredJob.Of(database, "sync")?.Outcome == "Failed", "the failed run of the job");

        var status = await OfAsync(application, "sync");

        // It is not tried again before its time, so it is due as after a run that did its work.
        Assert.Equal(BackgroundJobRunState.Failed, status.State);
        Assert.Equal(Start + Hourly, status.DueAt);
        Assert.False(status.Overdue);
    }

    [Fact]
    public async Task A_job_that_has_not_run_has_no_run_and_is_not_overdue()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();
        using var other = HoldingTheLock(database);

        using var application = ApplicationWithJobs.On(
            database, new TestClock(Start), new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly)));

        var status = await OfAsync(application, "sync");

        // It is due at once, and since when is not known: nothing says that it is late.
        Assert.Equal(
            new BackgroundJobStatus(
                "sync", status.Schedule, BackgroundJobRunState.NoRunYet, LastStartedAt: null, LastFinishedAt: null, DueAt: null, Overdue: false),
            status);
        Assert.NotNull(status.Schedule);
    }

    [Fact]
    public async Task A_run_is_overdue_once_it_was_due_for_longer_than_a_run_takes_to_start()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();
        using var other = HoldingTheLock(database);

        // A run that started and has no end: it is still going, or the process ended before it.
        Seed(database, "sync", Start, finishedAt: null, outcome: null);

        var clock = new TestClock(Start + Hourly + TimeSpan.FromSeconds(30));
        using var application = ApplicationWithJobs.On(database, clock, new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly)));

        var due = await OfAsync(application, "sync");

        Assert.Equal(BackgroundJobRunState.NotEnded, due.State);
        Assert.Null(due.LastFinishedAt);
        Assert.Equal(Start + Hourly, due.DueAt);
        Assert.False(due.Overdue);

        clock.Set(Start + Hourly + TimeSpan.FromSeconds(31));

        Assert.True((await OfAsync(application, "sync")).Overdue);
    }

    [Fact]
    public async Task A_run_on_an_interval_that_was_stopped_is_due_from_when_it_was_stopped()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();
        using var other = HoldingTheLock(database);

        var stoppedAt = Start.AddMinutes(10);
        Seed(database, "sync", Start, stoppedAt, "Stopped");

        var clock = new TestClock(stoppedAt.AddSeconds(5));
        using var application = ApplicationWithJobs.On(database, clock, new RecordingJob("sync", BackgroundJobSchedule.Every(Hourly)));

        var status = await OfAsync(application, "sync");

        // The run left its work to do, so the job does not wait for an hour from its start.
        Assert.Equal(BackgroundJobRunState.Stopped, status.State);
        Assert.Equal(stoppedAt, status.DueAt);
        Assert.False(status.Overdue);

        clock.Set(stoppedAt.AddMinutes(1));

        Assert.True((await OfAsync(application, "sync")).Overdue);
    }

    [Fact]
    public async Task A_job_on_boundaries_is_due_at_the_boundary_after_its_latest_start()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();
        using var other = HoldingTheLock(database);

        // Started a moment after the boundary of 12:15, and stopped: a boundary is not made up for.
        var startedAt = new DateTimeOffset(2026, 11, 11, 12, 15, 0, 200, TimeSpan.Zero);
        Seed(database, "cycle", startedAt, startedAt.AddMinutes(2), "Stopped");

        var clock = new TestClock(startedAt.AddMinutes(3));
        var quarterly = BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15));
        using var application = ApplicationWithJobs.On(database, clock, new RecordingJob("cycle", quarterly));

        var status = await OfAsync(application, "cycle");

        Assert.True(status.Schedule!.OnUtcBoundaries);
        Assert.Equal(TimeSpan.FromMinutes(15), status.Schedule.Period);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 12, 30, 0, TimeSpan.Zero), status.DueAt);
        Assert.False(status.Overdue);

        // The boundary passed with no run.
        clock.Set(new DateTimeOffset(2026, 11, 11, 12, 31, 0, TimeSpan.Zero));

        Assert.True((await OfAsync(application, "cycle")).Overdue);
    }

    [Fact]
    public async Task The_jobs_are_listed_by_name_and_a_run_of_a_job_that_is_not_registered_comes_last()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();
        using var other = HoldingTheLock(database);

        // A job of an earlier version, whose name would come first.
        Seed(database, "a-job-that-is-gone", Start, Start.AddSeconds(1), "Succeeded");

        using var application = ApplicationWithJobs.On(
            database,
            new TestClock(Start.AddDays(3)),
            new RecordingJob("zebra", BackgroundJobSchedule.Every(Hourly)),
            new RecordingJob("apple", BackgroundJobSchedule.Every(Hourly)));

        var all = await application.Services.GetRequiredService<IBackgroundWorkStatus>().ReadAsync(TestContext.Current.CancellationToken);
        var names = all.Select(job => job.Name).ToList();

        // The jobs of this application first, by name, with the two that every application of a
        // test has. Then the one that only the database knows.
        Assert.Equal(names[..^1].Order(StringComparer.Ordinal), names[..^1]);
        Assert.Contains("apple", names);
        Assert.Contains("zebra", names);
        Assert.Equal("a-job-that-is-gone", names[^1]);

        // It has no schedule here, so nothing says when it is due, and it is never overdue.
        var gone = all[^1];
        Assert.Null(gone.Schedule);
        Assert.Equal(BackgroundJobRunState.Succeeded, gone.State);
        Assert.Null(gone.DueAt);
        Assert.False(gone.Overdue);

        Assert.All(all.Where(job => job.Name != gone.Name), job => Assert.NotNull(job.Schedule));
    }

    private static async Task<BackgroundJobStatus> OfAsync(LoggedApplication application, string name)
    {
        var all = await application.Services.GetRequiredService<IBackgroundWorkStatus>().ReadAsync(TestContext.Current.CancellationToken);

        return Assert.Single(all, job => job.Name == name);
    }

    /// <summary>Another instance, as far as the database can tell: a session that holds the lock until it is disposed.</summary>
    private static NpgsqlConnection HoldingTheLock(TestDatabase database)
    {
        var session = database.Open();

        using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", session);
        command.Parameters.AddWithValue("key", BackgroundJobs.LockKey);
        command.ExecuteNonQuery();

        return session;
    }

    /// <summary>Puts the latest run of a job in a database, as the scheduler would have left it.</summary>
    private static void Seed(TestDatabase database, string name, DateTimeOffset startedAt, DateTimeOffset? finishedAt, string? outcome)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO background_jobs (name, last_started_at, last_finished_at, last_outcome) VALUES (@name, @started, @finished, @outcome)",
            connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("started", startedAt);
        command.Parameters.AddWithValue("finished", (object?)finishedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome", (object?)outcome ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
