using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Jobs;

/// <summary>A job whose body a test supplies, on a short interval unless the test wants otherwise.</summary>
internal sealed class TestJob(
    string name,
    Func<JobContext, CancellationToken, Task>? work = null,
    TimeSpan? interval = null,
    TimeSpan? timeout = null) : IRecurringJob
{
    private int _starts;

    public int Starts => Volatile.Read(ref _starts);

    public string Name => name;

    public JobSchedule Schedule { get; } = JobSchedule.Every(interval ?? TimeSpan.FromMilliseconds(50));

    public TimeSpan Timeout { get; } = timeout ?? System.Threading.Timeout.InfiniteTimeSpan;

    public Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _starts);

        return work is null ? Task.CompletedTask : work(context, cancellationToken);
    }
}

/// <summary>Holds work at a known point, so a test never has to guess how far it got.</summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    /// <summary>For the test. It has a deadline, so that a broken runner fails rather than hangs.</summary>
    public Task UntilReachedAsync() => _reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    /// <summary>For the work: says it's here.</summary>
    public void Arrive() => _reached.TrySetResult();

    /// <summary>For the work: says it's here, then waits. It ignores cancellation on purpose.</summary>
    public Task PassAsync()
    {
        Arrive();

        return _opened.Task;
    }

    public void Open() => _opened.TrySetResult();
}

/// <summary>The real application with some jobs registered, started on a database of a test's choosing.</summary>
internal sealed class JobApp : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    public JobApp(string database, Action<IServiceCollection>? configureServices, params IRecurringJob[] jobs)
    {
        App = new AppFactory(
            database,
            services =>
            {
                foreach (var job in jobs)
                {
                    services.AddSingleton(job);
                }

                configureServices?.Invoke(services);
            },
            new Dictionary<string, string?> { ["Logging:Sinks:Console:Enabled"] = "false" });

        App.Start();

        Runner = App.Services.GetRequiredService<IJobRunner>();
        Lifetime = App.Services.GetRequiredService<IHostApplicationLifetime>();
    }

    public JobApp(string database, params IRecurringJob[] jobs)
        : this(database, null, jobs)
    {
    }

    public AppFactory App { get; }

    public IJobRunner Runner { get; }

    public IHostApplicationLifetime Lifetime { get; }

    /// <summary>Starts a shutdown the way SIGTERM does, and doesn't wait for it.</summary>
    public Task BeginStoppingAsync()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Lifetime.ApplicationStopped.Register(stopped.SetResult);
        Lifetime.StopApplication();

        return stopped.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}

/// <summary>Reads what the runner recorded, straight from the database.</summary>
internal static class JobRows
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A database that has its schema but has never run anything, for seeding rows.</summary>
    public static async Task<string> MigratedDatabaseAsync(PostgresFixture postgres)
    {
        var database = postgres.NewConnectionString();

        await using (new JobApp(database))
        {
        }

        return database;
    }

    public static Task SeedAsync(string database, string job, DateTimeOffset startedAt, JobStatus status) =>
        Sql.ExecuteAsync(
            database,
            $"INSERT INTO job_runs (job_name, started_at, status) VALUES ('{job}', '{startedAt.UtcDateTime:O}', '{status}')",
            Cancellation);

    public static Task<List<string>> StatusesAsync(string database, string job) =>
        Sql.ColumnAsync(database, $"SELECT status FROM job_runs WHERE job_name = '{job}' ORDER BY id", Cancellation);

    /// <summary>Everything stored about a job's runs, in one string to scan.</summary>
    public static async Task<string> TextAsync(string database, string job)
    {
        var rows = await Sql.ColumnAsync(
            database,
            $"SELECT concat_ws(' ', status, details, correlation_id) FROM job_runs WHERE job_name = '{job}' ORDER BY id",
            Cancellation);

        return string.Join('\n', rows);
    }

    public static Task<double> SecondsBetweenFirstAndLastStartAsync(string database, string job) =>
        Sql.ScalarAsync<double>(
            database,
            $"SELECT extract(epoch FROM max(started_at) - min(started_at))::float8 FROM job_runs WHERE job_name = '{job}'",
            Cancellation);

    public static Task UntilStatusAsync(string database, string job, JobStatus status, int count = 1) =>
        LoggedApp.UntilAsync(async () =>
            (await StatusesAsync(database, job)).Count(value => value == status.ToString()) >= count);
}
