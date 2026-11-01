using System.Diagnostics;
using Ecnanib.Platform.Contracts;
using Ecnanib.Platform.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Platform.Jobs;

/// <summary>
/// Runs recurring and ad-hoc work inside the process, with the history and schedules in job_runs.
/// Each recurring job gets its own loop, and each run its own tasks, so nothing here can be held up
/// by another job. See docs/architecture.md for the shutdown timeline.
/// </summary>
internal sealed class JobRunner(
    IServiceProvider services,
    JobRunStore store,
    ICorrelationContext correlation,
    IHostApplicationLifetime lifetime,
    IOptions<HostOptions> hostOptions,
    IConfiguration configuration,
    ILogger<JobRunner> logger) : IJobRunner, IHostedService, IDisposable
{
    /// <summary>
    /// How long the host gets to stop, set in PlatformModule. Jobs get the first two-thirds, which
    /// must cover an exchange call, and the rest is for unwinding and recording what happened.
    /// </summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long work gets to respond to its token before its outcome is recorded without it.</summary>
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FinishAttemptLimit = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan FinishRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>A single long delay would miss clock corrections, and Task.Delay throws past 49 days.</summary>
    private static readonly TimeSpan LongestSleep = TimeSpan.FromMinutes(1);

    private readonly SecretRules _rules = SecretRules.From(configuration);

    private readonly Lock _lock = new();

    /// <summary>Runs still going, with their job names. A run stays here until its work has really ended.</summary>
    private readonly Dictionary<Task, string> _runs = [];

    private readonly List<Task> _loops = [];

    /// <summary>Shutdown has begun: start nothing new.</summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The drain window is over, so work still running is cancelled.</summary>
    private readonly CancellationTokenSource _drainOver = new();

    /// <summary>Time's up for everything, including writing down what happened.</summary>
    private readonly CancellationTokenSource _shutdownOver = new();

    private IRecurringJob[] _jobs = [];
    private Task? _stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolved here, because a constructor parameter would make a cycle of any job that depends
        // on a service that uses IJobRunner.
        _jobs = [.. services.GetServices<IRecurringJob>()];

        RefuseInvalidJobs();

        await store.InterruptAbandonedRunsAsync(cancellationToken);

        // Started, not here: any hosted service's own startup finishes before the first scheduled
        // run, and if startup fails, nothing has run.
        lifetime.ApplicationStarted.Register(StartLoops);

        // Stopping, not StopAsync: services stop one at a time, and the web server goes first with
        // the same token. If it spent that, a drain timed from StopAsync would get nothing.
        lifetime.ApplicationStopping.Register(BeginStopping);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Under a test host this is called twice at once, and a second caller that returned early
        // would dispose the services while the drain still needs them. The token is ignored because
        // the web server may have spent it already. Shutdown has its own clock.
        lock (_lock)
        {
            return _stopped ??= Task.Run(DrainAsync);
        }
    }

    public Task<JobStatus> RunAsync(string name, TimeSpan timeout, Func<JobContext, CancellationToken, Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        RefuseInvalidTimeout(timeout);

        lock (_lock)
        {
            if (_stopping.IsCancellationRequested)
            {
                return Task.FromResult(JobStatus.NotStarted);
            }

            var run = Task.Run(() => RunAndRecordAsync(name, timeout, work));

            _runs.Add(run, name);
            run.ContinueWith(Forget, TaskScheduler.Default);

            return run;
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _drainOver.Dispose();
        _shutdownOver.Dispose();
    }

    private void RefuseInvalidJobs()
    {
        foreach (var job in _jobs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);
            RefuseInvalidTimeout(job.Timeout);
        }

        var duplicates = _jobs.GroupBy(job => job.Name).Where(group => group.Count() > 1).Select(group => group.Key).ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"These job names are registered more than once: {string.Join(", ", duplicates)}. "
                + "Each job needs its own, because its schedule is found by name.");
        }
    }

    private static void RefuseInvalidTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        }
    }

    private void StartLoops()
    {
        lock (_lock)
        {
            if (!_stopping.IsCancellationRequested)
            {
                _loops.AddRange(_jobs.Select(job => Task.Run(() => RunOnScheduleAsync(job))));
            }
        }
    }

    /// <summary>
    /// Runs in the lifetime's callback, under its lock, so nothing here may block. That's why this
    /// uses CancelAsync: a plain Cancel would run the jobs' callbacks right here.
    /// </summary>
    private void BeginStopping()
    {
        lock (_lock)
        {
            _ = _stopping.CancelAsync();
        }

        var total = hostOptions.Value.ShutdownTimeout;

        _drainOver.CancelAfter(total * 2 / 3);
        _shutdownOver.CancelAfter(total);
    }

    /// <summary>Never throws: the host rethrows from app.Run(), which would fail a clean stop.</summary>
    private async Task DrainAsync()
    {
        Task everything;

        lock (_lock)
        {
            everything = Task.WhenAll([.. _loops, .. _runs.Keys]);
        }

        await everything.WaitAsync(_shutdownOver.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        lock (_lock)
        {
            // Filtered, because a finished run can still be here until its continuation has run.
            var going = _runs.Where(run => !run.Key.IsCompleted).Select(run => run.Value).ToList();

            if (going.Count > 0)
            {
                logger.LogError("Stopping with job runs still going: {JobNames}", going);
            }
        }
    }

    private void Forget(Task run)
    {
        lock (_lock)
        {
            _runs.Remove(run);
        }
    }

    /// <summary>A failure in here must not end the loop, or the job would stop running for good.</summary>
    private async Task RunOnScheduleAsync(IRecurringJob job)
    {
        DateTimeOffset? lastStart = null;
        var lastStartIsKnown = false;

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                if (!lastStartIsKnown)
                {
                    lastStart = await store.LastStartAsync(job.Name, _stopping.Token);
                    lastStartIsKnown = true;
                }

                var now = DateTimeOffset.UtcNow;
                var wait = job.Schedule.NextRun(lastStart, now) - now;

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(Shorter(wait, LongestSleep), _stopping.Token);
                    continue;
                }

                if (await RunAsync(job.Name, job.Timeout, job.RunAsync) == JobStatus.NotStarted)
                {
                    await Task.Delay(Shorter(job.Schedule.NextRun(now, now) - now, StartRetryDelay), _stopping.Token);
                    continue;
                }

                lastStart = now;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Broad because whatever went wrong, the loop has to survive it.
                logger.LogError(exception, "The schedule of job {JobName} failed, and will try again", job.Name);

                await Task.Delay(StartRetryDelay, _stopping.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task<JobStatus> RunAndRecordAsync(
        string name,
        TimeSpan timeout,
        Func<JobContext, CancellationToken, Task> work)
    {
        using var run = correlation.Begin();

        long id;

        try
        {
            id = await store.StartAsync(name, correlation.CurrentId, _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return JobStatus.NotStarted;
        }
        catch (Exception exception)
        {
            // Broad because a run nobody recorded must not trade, whatever stopped the record.
            logger.LogError(exception, "Job {JobName} didn't run, because its start couldn't be recorded", name);

            return JobStatus.NotStarted;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_drainOver.Token);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, deadline.Token);
        using var abandon = new CancellationTokenSource();
        await using var grace = deadline.Token.Register(() => abandon.CancelAfter(CancellationGrace));

        deadline.CancelAfter(timeout);

        var context = new JobContext(stopping.Token);
        var started = Stopwatch.GetTimestamp();

        // Its own task, so that work that blocks a thread can't stop this method recording the outcome.
        var working = Task.Run(() => work(context, deadline.Token));

        var (status, details, failure) = await AwaitOutcomeAsync(working, context, deadline.Token, abandon.Token);

        // Logged first, so the true outcome is on record even if the database can't take it.
        logger.Log(
            LevelFor(status),
            failure,
            "Job {JobName} finished as {JobStatus} in {DurationMs:0.0} ms",
            name,
            status,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        await RecordFinishAsync(id, name, status, details);

        if (!working.IsCompleted)
        {
            logger.LogError("Job {JobName} ignored its cancellation, and holds its place until it ends", name);

            await working.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return status;
    }

    private async Task<(JobStatus Status, string? Details, Exception? Failure)> AwaitOutcomeAsync(
        Task working,
        JobContext context,
        CancellationToken deadline,
        CancellationToken abandon)
    {
        try
        {
            await working.WaitAsync(abandon);

            return (JobStatus.Succeeded, Scrubbed(context.Details), null);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
            // Only these tokens mean that. A job's own HttpClient timeout is a failure like any other.
            var status = _stopping.IsCancellationRequested ? JobStatus.Interrupted : JobStatus.TimedOut;

            return (status, Scrubbed(context.Details), null);
        }
        catch (Exception exception)
        {
            // Broad because this is the boundary that keeps a job's failure from reaching anything else.
            return (JobStatus.Failed, exception.GetType().FullName, exception);
        }
    }

    /// <summary>
    /// Keeps trying until the outage ends or shutdown's time runs out, because an unclosed row would
    /// be reported as interrupted for good. The caller holds its slot meanwhile. No attempt uses a job
    /// token, which is likely to have fired, and WaitAsync is there because Npgsql's own cancellation
    /// can wait on a silent server.
    /// </summary>
    private async Task RecordFinishAsync(long id, string name, JobStatus status, string? details)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        var reported = false;

        while (!_shutdownOver.IsCancellationRequested)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_shutdownOver.Token);
                attempt.CancelAfter(FinishAttemptLimit);

                await store.FinishAsync(id, status, details, finishedAt, attempt.Token).WaitAsync(attempt.Token);

                return;
            }
            catch (Exception exception)
            {
                // Broad because any failure here has the same answer: try again.
                if (!reported)
                {
                    logger.LogError(exception, "Couldn't record the end of job {JobName} run {JobRunId}, so retrying", name, id);
                    reported = true;
                }
            }

            await Task.Delay(FinishRetryDelay, _shutdownOver.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        logger.LogError("Gave up recording the end of job {JobName} run {JobRunId}", name, id);
    }

    private string? Scrubbed(string? details) => details is null ? null : _rules.Scrub(details);

    private static TimeSpan Shorter(TimeSpan first, TimeSpan second) => first < second ? first : second;

    private static LogLevel LevelFor(JobStatus status) => status switch
    {
        JobStatus.Succeeded => LogLevel.Information,
        JobStatus.Failed => LogLevel.Error,
        _ => LogLevel.Warning,
    };
}
