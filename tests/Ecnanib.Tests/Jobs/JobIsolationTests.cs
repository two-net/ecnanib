using System.Net;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Jobs;

/// <summary>
/// NFR-REL-05. Agents and providers don't exist yet, so these prove what they'll rely on: that a
/// failure stays inside its own run, whichever way it fails.
/// </summary>
public sealed class JobIsolationTests(PostgresFixture postgres)
{
    private const string Bad = "bad";
    private const string Good = "good";

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Unlimited = Timeout.InfiniteTimeSpan;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_throwing_job_is_recorded_and_runs_again_while_others_carry_on(bool beforeFirstAwait)
    {
        var database = postgres.NewConnectionString();

        var bad = new TestJob(Bad, async (_, _) =>
        {
            if (!beforeFirstAwait)
            {
                await Task.Yield();
            }

            throw new InvalidOperationException("Broken");
        });

        await using var app = new JobApp(database, bad, new TestJob(Good));

        await JobRows.UntilStatusAsync(database, Bad, JobStatus.Failed, count: 2);
        await JobRows.UntilStatusAsync(database, Good, JobStatus.Succeeded, count: 2);

        using var client = app.App.CreateClient();
        using var live = await client.GetAsync("/health/live", Cancellation);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Contains("Failed System.InvalidOperationException", await JobRows.TextAsync(database, Bad), StringComparison.Ordinal);
    }

    /// <summary>Both tokens have to fire: a job passes Stopping to an AI call, and it must not outlive the run.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hung_job_times_out_and_the_next_run_goes_ahead(bool watchingStopping)
    {
        var database = postgres.NewConnectionString();

        var hung = new TestJob(
            Bad,
            (context, cancellationToken) =>
                Task.Delay(Unlimited, watchingStopping ? context.Stopping : cancellationToken),
            timeout: Short);

        await using var app = new JobApp(database, hung, new TestJob(Good));

        await JobRows.UntilStatusAsync(database, Bad, JobStatus.TimedOut, count: 2);
        await JobRows.UntilStatusAsync(database, Good, JobStatus.Succeeded, count: 2);
    }

    /// <summary>
    /// A stall must not be silent, but a second copy must not start either: two concurrent reconcilers
    /// are worse than a stalled one (NFR-REL-03).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Work_that_ignores_its_token_is_recorded_on_time_and_keeps_its_place(bool blockingAThread)
    {
        var database = postgres.NewConnectionString();
        var gate = new Gate();
        using var blocked = new ManualResetEventSlim();

        var stuck = new TestJob(
            Bad,
            async (_, _) =>
            {
                if (blockingAThread)
                {
                    blocked.Wait();
                }
                else
                {
                    await gate.PassAsync();
                }
            },
            timeout: Short);

        await using var app = new JobApp(database, stuck, new TestJob(Good));

        try
        {
            await JobRows.UntilStatusAsync(database, Bad, JobStatus.TimedOut);

            Assert.Equal(1, stuck.Starts);
            await JobRows.UntilStatusAsync(database, Good, JobStatus.Succeeded, count: 2);
            Assert.Equal(1, stuck.Starts);
        }
        finally
        {
            blocked.Set();
            gate.Open();
        }

        await LoggedApp.UntilAsync(() => ValueTask.FromResult(stuck.Starts > 1));
    }

    [Fact]
    public async Task One_failure_in_a_fan_out_leaves_the_others_alone()
    {
        await using var app = new JobApp(postgres.NewConnectionString());

        var statuses = await Task.WhenAll(
            app.Runner.RunAsync("agent-1", Unlimited, (_, _) => Task.CompletedTask),
            app.Runner.RunAsync("agent-2", Unlimited, (_, _) => throw new InvalidOperationException("Broken")),
            app.Runner.RunAsync("agent-3", Unlimited, (_, _) => Task.CompletedTask));

        Assert.Equal([JobStatus.Succeeded, JobStatus.Failed, JobStatus.Succeeded], statuses);
    }

    /// <summary>An HttpClient timeout looks like this. Calling it cancelled would hide a real failure.</summary>
    [Fact]
    public async Task A_cancellation_the_runner_didnt_ask_for_is_a_failure()
    {
        await using var app = new JobApp(postgres.NewConnectionString());

        var status = await app.Runner.RunAsync(Bad, Unlimited, (_, _) => throw new TaskCanceledException("Timed out"));

        Assert.Equal(JobStatus.Failed, status);
    }

    /// <summary>A run nobody recorded must not trade, and the job must come back without a restart.</summary>
    [Fact]
    public async Task With_the_database_down_work_doesnt_run_and_recovers_on_its_own()
    {
        var database = await postgres.CreateDatabaseAsync(Cancellation);
        var job = new TestJob(Good);
        var ran = false;

        await using var app = new JobApp(database, job);

        await LoggedApp.UntilAsync(() => ValueTask.FromResult(job.Starts > 0));
        await postgres.RefuseConnectionsAsync(database, Cancellation);

        var status = await app.Runner.RunAsync(Bad, Unlimited, (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        var startsDuringTheOutage = job.Starts;

        await postgres.AllowConnectionsAsync(database, Cancellation);
        await LoggedApp.UntilAsync(() => ValueTask.FromResult(job.Starts > startsDuringTheOutage));

        Assert.Equal(JobStatus.NotStarted, status);
        Assert.False(ran);
    }

    /// <summary>An unclosed row would be reported as interrupted for good, though the work succeeded.</summary>
    [Fact]
    public async Task A_run_that_ends_during_an_outage_is_recorded_when_it_passes()
    {
        var database = await postgres.CreateDatabaseAsync(Cancellation);
        var gate = new Gate();

        await using var app = new LoggedApp(
            database,
            new Dictionary<string, string?> { ["Logging:Sinks:Database:Enabled"] = "false" },
            services => services.AddSingleton<IRecurringJob>(
                new TestJob(Bad, (_, _) => gate.PassAsync(), interval: TimeSpan.FromHours(1))));

        await gate.UntilReachedAsync();
        await postgres.RefuseConnectionsAsync(database, Cancellation);

        gate.Open();

        await LoggedApp.UntilAsync(() => ValueTask.FromResult(
            app.FileText().Contains("Couldn't record the end of job", StringComparison.Ordinal)));

        await postgres.AllowConnectionsAsync(database, Cancellation);
        await JobRows.UntilStatusAsync(database, Bad, JobStatus.Succeeded);
    }
}
