using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Jobs;

/// <summary>
/// The database is the schedule's memory, and what startup does with it. To prove a job waited, these
/// compare real start times rather than sleep and assert that nothing happened.
/// </summary>
public sealed class JobDurabilityTests(PostgresFixture postgres)
{
    private const string Job = "durable";

    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recent_start_makes_a_job_wait_out_its_interval()
    {
        var database = await JobRows.MigratedDatabaseAsync(postgres);
        var interval = TimeSpan.FromSeconds(2);

        await JobRows.SeedAsync(database, Job, DateTimeOffset.UtcNow, JobStatus.Succeeded);

        await using var app = new JobApp(database, new TestJob(Job, interval: interval));

        await JobRows.UntilStatusAsync(database, Job, JobStatus.Succeeded, count: 2);

        Assert.True(await JobRows.SecondsBetweenFirstAndLastStartAsync(database, Job) >= interval.TotalSeconds);
    }

    /// <summary>A failed run counts as a start, or a job that fails at once would spin.</summary>
    [Fact]
    public async Task A_failed_start_counts_too()
    {
        var database = await JobRows.MigratedDatabaseAsync(postgres);
        var interval = TimeSpan.FromSeconds(2);

        await JobRows.SeedAsync(database, Job, DateTimeOffset.UtcNow, JobStatus.Failed);

        await using var app = new JobApp(database, new TestJob(Job, interval: interval));

        await JobRows.UntilStatusAsync(database, Job, JobStatus.Succeeded);

        Assert.True(await JobRows.SecondsBetweenFirstAndLastStartAsync(database, Job) >= interval.TotalSeconds);
    }

    [Fact]
    public async Task A_late_job_runs_at_once_and_only_once()
    {
        var database = await JobRows.MigratedDatabaseAsync(postgres);
        var job = new TestJob(Job, interval: Hourly);

        await JobRows.SeedAsync(database, Job, DateTimeOffset.UtcNow.AddDays(-10), JobStatus.Succeeded);

        await using (new JobApp(database, job))
        {
            await JobRows.UntilStatusAsync(database, Job, JobStatus.Succeeded, count: 2);
        }

        Assert.Equal(1, job.Starts);
    }

    /// <summary>An in-memory timer would never fire if the process restarted more often than its interval.</summary>
    [Fact]
    public async Task A_restart_doesnt_restart_the_clock()
    {
        var database = postgres.NewConnectionString();
        var interval = TimeSpan.FromSeconds(3);

        await using (new JobApp(database, new TestJob(Job, interval: interval)))
        {
            await JobRows.UntilStatusAsync(database, Job, JobStatus.Succeeded);
        }

        await using var restarted = new JobApp(database, new TestJob(Job, interval: interval));

        await JobRows.UntilStatusAsync(database, Job, JobStatus.Succeeded, count: 2);

        Assert.True(await JobRows.SecondsBetweenFirstAndLastStartAsync(database, Job) >= interval.TotalSeconds);
    }

    [Fact]
    public async Task Runs_left_by_a_crash_are_closed_before_any_job_starts()
    {
        var database = await JobRows.MigratedDatabaseAsync(postgres);
        var seenByTheJob = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await JobRows.SeedAsync(database, Job, DateTimeOffset.UtcNow.AddHours(-2), JobStatus.Running);
        await JobRows.SeedAsync(database, "no-longer-registered", DateTimeOffset.UtcNow.AddHours(-2), JobStatus.Running);

        await using var app = new JobApp(database, new TestJob(
            Job,
            async (_, cancellationToken) => seenByTheJob.TrySetResult(
                await Sql.ColumnAsync(
                    database,
                    "SELECT status FROM job_runs WHERE details IS NOT NULL ORDER BY id",
                    cancellationToken)),
            interval: Hourly));

        Assert.Equal(["Interrupted", "Interrupted"], await seenByTheJob.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation));
        Assert.Equal(
            2,
            await Sql.ScalarAsync<long>(
                database,
                "SELECT count(*) FROM job_runs WHERE status = 'Interrupted' AND finished_at IS NULL",
                Cancellation));
    }

    /// <summary>
    /// FR-EXE-08 needs reconciliation before cycles resume. A hosted service's own startup comes first,
    /// so that's where it can happen, and it needs to be recorded like any other run.
    /// </summary>
    [Fact]
    public async Task A_hosted_service_can_finish_a_run_before_any_scheduled_job_starts()
    {
        var database = postgres.NewConnectionString();
        var job = new TestJob(Job);
        var startup = new StartupPass(job);

        await using var app = new JobApp(
            database,
            services => services.AddSingleton<IHostedService>(provider =>
            {
                startup.Runner = provider.GetRequiredService<IJobRunner>();
                return startup;
            }),
            job);

        await LoggedApp.UntilAsync(() => ValueTask.FromResult(job.Starts > 0));

        Assert.Equal(JobStatus.Succeeded, startup.Status);
        Assert.Equal(0, startup.StartsAfterThePass);
    }

    [Fact]
    public async Task If_startup_fails_no_job_has_run()
    {
        var job = new TestJob(Job);

        await Assert.ThrowsAsync<StartupFailedException>(async () =>
        {
            await using var app = new JobApp(
                postgres.NewConnectionString(),
                services => services.AddSingleton<IHostedService, FailingService>(),
                job);
        });

        Assert.Equal(0, job.Starts);
    }

    [Fact]
    public async Task A_job_name_used_twice_stops_startup_and_is_named()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = new JobApp(postgres.NewConnectionString(), new TestJob("twice"), new TestJob("twice"));
        });

        Assert.Contains("twice", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task A_job_needs_a_name(string name) =>
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var app = new JobApp(postgres.NewConnectionString(), new TestJob(name));
        });

    [Fact]
    public async Task A_job_needs_a_usable_timeout() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using var app = new JobApp(postgres.NewConnectionString(), new TestJob(Job, timeout: TimeSpan.Zero));
        });

    private sealed class StartupPass(TestJob job) : IHostedService
    {
        public IJobRunner? Runner { get; set; }

        public JobStatus Status { get; private set; }

        public int StartsAfterThePass { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Status = await Runner!.RunAsync("startup-pass", Timeout.InfiniteTimeSpan, (_, _) => Task.CompletedTask);
            StartsAfterThePass = job.Starts;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new StartupFailedException();

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StartupFailedException : Exception;
}
