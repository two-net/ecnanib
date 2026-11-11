using System.Collections.Concurrent;
using System.Text.Json;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>What the scheduler writes to the log, and the area it writes under.</summary>
internal static class SchedulerLog
{
    public const string Area = "Ecnanib.Platform.BackgroundWork.BackgroundJobScheduler";

    public const string Active = "This instance runs the background work, with {JobCount} jobs";
    public const string StandingBy = "Another instance runs the background work. This one stands by, and takes over when that one stops";
    public const string Unfinished = "Background job {Job} did not finish the run it started at {StartedAt}. It runs again when it is next due, at {NextRun}";
    public const string Finished = "Background job {Job} finished in {DurationMs} ms";
    public const string Failed = "Background job {Job} failed after {DurationMs} ms. It runs again when it is next due";
    public const string StoppedRun = "Background job {Job} was stopped after {DurationMs} ms, before it was done";
    public const string Late = "Background job {Job} could not start its run due at {DueAt} before the following one was due, so that run is skipped";
    public const string NotStarted = "Background job {Job} could not be started, because its state could not be written. It is tried again every second";
    public const string StartedElsewhere = "Background job {Job} was started by another instance at {StartedAt}, so it does not start here";
    public const string Waits = "Background job {Job} is still running since {StartedAt}, so its run due at {DueAt} waits until it ends";
    public const string Skipped = "Background job {Job} is still running since {StartedAt}, so its run due at {DueAt} is skipped";
    public const string SessionEnded = "The database session that holds the background work lock has ended or does not answer, so this instance stops its jobs and stands by";
    public const string CannotGoOn = "The background work cannot go on. This instance tries again every second until it can";

    /// <summary>The events the scheduler has written so far, read while the application is still writing.</summary>
    public static IReadOnlyList<LoggedEvent> Of(LoggedApplication application)
    {
        try
        {
            return [.. application.Events().Where(written => written.Area == Area)];
        }
        catch (JsonException)
        {
            // A line that is only half written yet.
            return [];
        }
    }

    public static int Count(LoggedApplication application, string template) =>
        Of(application).Count(written => written.Template == template);

    /// <summary>What the scheduler wrote at the warning level or above.</summary>
    public static IReadOnlyList<LoggedEvent> Trouble(LoggedApplication application) =>
        [.. Of(application).Where(written => written.Level is "Warning" or "Error" or "Fatal")];
}

/// <summary>The application with jobs of the test's own, as a component would have registered them.</summary>
internal static class ApplicationWithJobs
{
    public static LoggedApplication On(TestDatabase database, params IBackgroundJob[] jobs) => On(database, clock: null, jobs);

    /// <param name="database">A database of the test's own: on a shared one, another application may hold the lock.</param>
    /// <param name="clock">The time the scheduler goes by, when the test sets it.</param>
    /// <param name="jobs">In the order the scheduler looks at them.</param>
    public static LoggedApplication On(TestDatabase database, TestClock? clock, params IBackgroundJob[] jobs) =>
        new(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services =>
            {
                foreach (var job in jobs)
                {
                    services.AddSingleton(job);
                }

                if (clock is not null)
                {
                    services.AddSingleton<TimeProvider>(clock);
                }
            }),
            database);
}

/// <summary>A job that records its runs, and does what the test gives it to do.</summary>
internal sealed class RecordingJob(
    string name,
    BackgroundJobSchedule schedule,
    Func<BackgroundJobRun, CancellationToken, Task>? work = null) : IBackgroundJob
{
    private readonly ConcurrentQueue<BackgroundJobRun> _runs = new();
    private int _stopped;

    public string Name => name;

    public BackgroundJobSchedule Schedule => schedule;

    /// <summary>The runs that have started, in order.</summary>
    public IReadOnlyList<BackgroundJobRun> Runs => [.. _runs];

    /// <summary>How many runs ended because the application canceled them.</summary>
    public int Stopped => Volatile.Read(ref _stopped);

    /// <summary>What a run does. A test can change it between runs.</summary>
    public Func<BackgroundJobRun, CancellationToken, Task>? Work { get; set; } = work;

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        _runs.Enqueue(run);

        try
        {
            if (Work is { } doWork)
            {
                await doWork(run, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _stopped);

            throw;
        }
    }

    /// <summary>Work that goes on until the application stops it.</summary>
    public static Task UntilStopped(BackgroundJobRun run, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}

/// <summary>
/// A clock that stands still until a test moves it. Only the time of day is the test's: waiting
/// takes real time, so the scheduler notices a moved clock within one of its beats.
/// </summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private long _ticks = now.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _ticks), TimeSpan.Zero);

    public void Set(DateTimeOffset now) => Volatile.Write(ref _ticks, now.UtcTicks);
}

/// <summary>A job's row of the <c>background_jobs</c> table, read by a test directly.</summary>
internal sealed record StoredJob(DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Outcome)
{
    public static StoredJob? Of(TestDatabase database, string name)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT last_started_at, last_finished_at, last_outcome FROM background_jobs WHERE name = @name", connection);
        command.Parameters.AddWithValue("name", name);
        using var reader = command.ExecuteReader();

        return reader.Read()
            ? new StoredJob(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    /// <summary>Puts a row in a database the application has not started on yet.</summary>
    public static void Seed(TestDatabase database, string name, DateTimeOffset startedAt)
    {
        database.Migrate();

        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO background_jobs (name, last_started_at) VALUES (@name, @started)", connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("started", startedAt);
        command.ExecuteNonQuery();
    }
}
