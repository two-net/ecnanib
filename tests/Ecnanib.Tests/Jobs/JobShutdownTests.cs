using System.Diagnostics;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Health;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Jobs;

/// <summary>
/// NFR-REL-04. Trades and exchange calls don't exist yet, so these prove what they'll rely on: time
/// to finish, a token that stays quiet meanwhile, and a record of how the run ended. They stop the
/// application with StopApplication, which is what ConsoleLifetime does on SIGTERM.
/// </summary>
public sealed class JobShutdownTests(PostgresFixture postgres)
{
    private const string Job = "shutdown";

    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The drain window is two-thirds of this.</summary>
    private static Action<IServiceCollection> ShutdownOf(TimeSpan timeout) =>
        services => services.Configure<HostOptions>(options => options.ShutdownTimeout = timeout);

    [Fact]
    public async Task The_host_allows_for_a_drain_and_for_recording_it()
    {
        await using var app = new JobApp(postgres.NewConnectionString());

        var options = app.App.Services.GetRequiredService<IOptions<HostOptions>>();

        Assert.Equal(TimeSpan.FromSeconds(45), options.Value.ShutdownTimeout);
    }

    [Fact]
    public async Task An_in_flight_job_is_told_to_stop_and_left_to_finish()
    {
        var database = postgres.NewConnectionString();
        var running = new Gate();
        var cancelledDuringTheCall = true;

        await using var app = new JobApp(database, new TestJob(
            Job,
            async (context, cancellationToken) =>
            {
                running.Arrive();
                await WhenFiredAsync(context.Stopping);

                // Stands in for an exchange call, given the token a job would naturally pass along.
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                cancelledDuringTheCall = cancellationToken.IsCancellationRequested;
            },
            interval: Hourly));

        await running.UntilReachedAsync();
        await app.BeginStoppingAsync();

        Assert.False(cancelledDuringTheCall);
        Assert.Equal(["Succeeded"], await JobRows.StatusesAsync(database, Job));
    }

    [Fact]
    public async Task A_job_that_stops_early_when_told_to_is_interrupted()
    {
        var database = postgres.NewConnectionString();
        var running = new Gate();

        await using var app = new JobApp(database, new TestJob(
            Job,
            (context, _) =>
            {
                running.Arrive();
                return Task.Delay(Timeout.InfiniteTimeSpan, context.Stopping);
            },
            interval: Hourly));

        await running.UntilReachedAsync();
        await app.BeginStoppingAsync();

        Assert.Equal(["Interrupted"], await JobRows.StatusesAsync(database, Job));
    }

    [Fact]
    public async Task A_job_still_running_when_the_drain_window_ends_is_cancelled_and_interrupted()
    {
        var database = postgres.NewConnectionString();
        var running = new Gate();

        await using var app = new JobApp(
            database,
            ShutdownOf(TimeSpan.FromSeconds(3)),
            new TestJob(
                Job,
                (_, cancellationToken) =>
                {
                    running.Arrive();
                    return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                interval: Hourly));

        await running.UntilReachedAsync();

        var started = Stopwatch.GetTimestamp();
        await app.BeginStoppingAsync();

        Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(30));
        Assert.Equal(["Interrupted"], await JobRows.StatusesAsync(database, Job));
    }

    [Fact]
    public async Task Nothing_starts_once_shutdown_has_begun()
    {
        var database = postgres.NewConnectionString();
        var running = new Gate();
        var job = new TestJob(Job, (_, _) => running.PassAsync());
        var ran = false;

        await using var app = new JobApp(database, job);

        await running.UntilReachedAsync();

        var stopped = app.BeginStoppingAsync();

        var status = await app.Runner.RunAsync("late", Timeout.InfiniteTimeSpan, (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        running.Open();
        await stopped;

        Assert.Equal(JobStatus.NotStarted, status);
        Assert.False(ran);
        Assert.Equal(1, job.Starts);
        Assert.Empty(await JobRows.StatusesAsync(database, "late"));
    }

    /// <summary>
    /// Nothing enforces the host's timeout, so a job like this would hold the process until a
    /// supervisor kills it. The row stays open, and the next start closes it.
    /// </summary>
    [Fact]
    public async Task A_job_that_ignores_everything_cant_hold_up_the_host()
    {
        var database = postgres.NewConnectionString();
        var stuck = new Gate();

        try
        {
            await using (var app = new JobApp(
                database,
                ShutdownOf(TimeSpan.FromSeconds(1)),
                new TestJob(Job, (_, _) => stuck.PassAsync(), interval: Hourly)))
            {
                await stuck.UntilReachedAsync();

                var stopped = app.BeginStoppingAsync();

                // Called directly because the host's own call is on another thread, and a throw there
                // would only show as a non-zero exit from app.Run().
                await RunnerOf(app).StopAsync(CancellationToken.None);
                await stopped;
            }

            await using (new JobApp(database))
            {
                Assert.Equal(["Interrupted"], await JobRows.StatusesAsync(database, Job));
            }
        }
        finally
        {
            stuck.Open();
        }
    }

    /// <summary>
    /// The web server stops first and shares the host's token, so it can spend all of it. A drain
    /// timed from the runner's own StopAsync would then get nothing, and start late.
    /// </summary>
    [Fact]
    public async Task A_stalled_web_server_doesnt_cost_jobs_their_drain()
    {
        var database = postgres.NewConnectionString();
        var running = new Gate();
        var toldToStop = new Gate();
        var stalled = new StalledService();

        await using var app = new JobApp(
            database,
            services => services.AddSingleton<IHostedService>(stalled),
            new TestJob(
                Job,
                async (context, _) =>
                {
                    running.Arrive();
                    await WhenFiredAsync(context.Stopping);
                    await toldToStop.PassAsync();
                },
                interval: Hourly));

        try
        {
            await running.UntilReachedAsync();

            var stopped = app.BeginStoppingAsync();

            await stalled.UntilReachedAsync();
            await toldToStop.UntilReachedAsync();

            toldToStop.Open();
            stalled.Release();
            await stopped;
        }
        finally
        {
            // The host doesn't enforce its timeout, so a failed test would otherwise hang on disposal.
            toldToStop.Open();
            stalled.Release();
        }

        Assert.Equal(["Succeeded"], await JobRows.StatusesAsync(database, Job));
    }

    /// <summary>
    /// The host's token may already be spent when the runner's turn comes. Under a test host stop is
    /// also called twice at once, and a second caller returning early would dispose the services
    /// that the first is still draining with.
    /// </summary>
    [Fact]
    public async Task Every_stop_waits_for_the_drain_even_with_a_spent_token()
    {
        var database = postgres.NewConnectionString();
        var running = new Gate();

        await using var app = new JobApp(database, new TestJob(Job, (_, _) => running.PassAsync(), interval: Hourly));

        await running.UntilReachedAsync();

        var stopped = app.BeginStoppingAsync();
        var second = RunnerOf(app).StopAsync(new CancellationToken(canceled: true));

        Assert.False(second.IsCompleted);

        running.Open();
        await second.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        await stopped;

        Assert.Equal(["Succeeded"], await JobRows.StatusesAsync(database, Job));
    }

    /// <summary>Npgsql's cancellation can itself wait on a server that has stopped answering.</summary>
    [Fact]
    public async Task A_silent_database_cant_hang_shutdown()
    {
        var database = postgres.NewConnectionString();
        var running = new Gate();
        var silent = new SilentDatabase();

        try
        {
            await using var app = new JobApp(
                database,
                services =>
                {
                    silent.AddTo(services);
                    ShutdownOf(TimeSpan.FromSeconds(2))(services);
                },
                new TestJob(Job, (_, _) => running.PassAsync(), interval: Hourly));

            await running.UntilReachedAsync();

            silent.Mute();
            running.Open();

            await app.BeginStoppingAsync();

            Assert.True(silent.Commands > 0);
        }
        finally
        {
            silent.Release();
        }
    }

    private static Task WhenFiredAsync(CancellationToken token) =>
        Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => { }, TaskScheduler.Default);

    private static IHostedService RunnerOf(JobApp app) =>
        app.App.Services.GetServices<IHostedService>().Single(service => service is IJobRunner);

    /// <summary>A service that stops before the runner and takes its time, whatever its token says.</summary>
    private sealed class StalledService : IHostedService
    {
        private readonly Gate _gate = new();

        public Task UntilReachedAsync() => _gate.UntilReachedAsync();

        public void Release() => _gate.Open();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => _gate.PassAsync();
    }
}
