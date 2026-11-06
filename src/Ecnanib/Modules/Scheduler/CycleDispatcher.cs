using System.Collections.Concurrent;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Scheduler;

/// <summary>
/// Starts cycles: after a random jitter, one per agent at a time, through <see cref="CycleSlots"/> (FR-CYC-08). Each
/// cycle is a run of its own, so it has its own correlation ID, error boundary and place in the shutdown drain
/// (NFR-REL-04, NFR-REL-05). See Scheduler in docs/architecture.md.
/// </summary>
internal sealed class CycleDispatcher(
    CycleSlots slots,
    IJobRunner jobs,
    IDecisionCycles cycles,
    SchedulerSettings settings,
    TimeProvider time,
    ILogger<CycleDispatcher> logger)
{
    /// <summary>The job name each cycle's run is recorded under. No recurring job may have it.</summary>
    public const string JobName = "decision-cycle";

    /// <summary>
    /// How long a run may go on past its deadline. After the deadline a cycle enters nothing (FR-CYC-06), but it may
    /// still be finishing what it started, such as recording its outcome.
    /// </summary>
    public static readonly TimeSpan WindDown = TimeSpan.FromMinutes(2);

    /// <summary>Longer than the runner gives work to answer its token before it records the outcome without it.</summary>
    private static readonly TimeSpan HungGrace = TimeSpan.FromSeconds(5);

    /// <summary>NFR-PERF-01: a cycle should start within this long after its close and the delay.</summary>
    public static readonly TimeSpan StartTarget = TimeSpan.FromSeconds(30);

    /// <summary>Agents with a cycle queued or running. Nothing else starts cycles, so this is the agent lock (FR-CYC-02).</summary>
    private readonly ConcurrentDictionary<Guid, byte> _busy = new();

    /// <summary>
    /// Starts a cycle for each request, and returns once each has started or been dropped. The cycles carry on
    /// after, so one that hangs can't hold up the next close.
    /// </summary>
    /// <param name="due">When these cycles were due: the close plus the delay.</param>
    /// <param name="stopping">Fires when shutdown begins. Whatever hasn't started by then never does.</param>
    public async Task<DispatchTally> DispatchAsync(IReadOnlyList<CycleRequest> requests, DateTimeOffset due, CancellationToken stopping)
    {
        var outcomes = await Task.WhenAll(requests.Select(request => DispatchAsync(request, due, stopping)));

        return new DispatchTally(
            outcomes.Count(outcome => outcome == DispatchOutcome.Started),
            outcomes.Count(outcome => outcome == DispatchOutcome.Busy),
            outcomes.Count(outcome => outcome == DispatchOutcome.PastDeadline),
            outcomes.Count(outcome => outcome == DispatchOutcome.NotStarted));
    }

    private async Task<DispatchOutcome> DispatchAsync(CycleRequest request, DateTimeOffset due, CancellationToken stopping)
    {
        using var scope = BeginScope(request);

        if (!_busy.TryAdd(request.AgentId, 0))
        {
            logger.LogWarning(
                "Skipped the decision cycle for the candle that closed at {CandleClose}, because the agent's previous cycle is still running",
                request.CandleClose);

            return DispatchOutcome.Busy;
        }

        var handedOver = false;

        try
        {
            await JitterAsync(stopping);

            var slot = await slots.WaitAsync(request.UserId, request.Deadline, stopping);

            if (slot is null)
            {
                logger.LogWarning(
                    "Dropped the decision cycle for the candle that closed at {CandleClose}, because it was still queued at its deadline, {Deadline}",
                    request.CandleClose,
                    request.Deadline);

                return DispatchOutcome.PastDeadline;
            }

            var (run, started) = Start(request, due, slot);
            handedOver = true;

            await Task.WhenAny(started, run);

            if (started.IsCompleted)
            {
                return DispatchOutcome.Started;
            }

            // The runner never invoked the work: shutdown had begun, or the run couldn't be recorded, and a run nobody
            // recorded must not trade. The runner has logged why.
            logger.LogWarning("The decision cycle for the candle that closed at {CandleClose} didn't start", request.CandleClose);

            return DispatchOutcome.NotStarted;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            logger.LogWarning("The decision cycle for the candle that closed at {CandleClose} didn't start, because the application is stopping", request.CandleClose);

            return DispatchOutcome.NotStarted;
        }
        catch (Exception exception)
        {
            // Broad because one agent's dispatch going wrong must not stop the others' (NFR-REL-05).
            logger.LogError(exception, "The decision cycle for the candle that closed at {CandleClose} couldn't be started", request.CandleClose);

            return DispatchOutcome.NotStarted;
        }
        finally
        {
            if (!handedOver)
            {
                _busy.TryRemove(request.AgentId, out _);
            }
        }
    }

    /// <summary>
    /// Starts the run, which keeps the slot and the agent's place until it has really ended. The second task
    /// completes when the runner invokes the cycle.
    /// </summary>
    private (Task<JobStatus> Run, Task Started) Start(CycleRequest request, DateTimeOffset due, CycleSlots.Slot slot)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var untilDeadline = request.Deadline - time.GetUtcNow();
        var timeout = (untilDeadline > TimeSpan.Zero ? untilDeadline : TimeSpan.Zero) + WindDown;
        Task<JobStatus> run;

        try
        {
            run = jobs.RunAsync(JobName, timeout, (context, cancellationToken) =>
            {
                started.TrySetResult();

                return RunAsync(request, due, context, cancellationToken);
            });
        }
        catch
        {
            slot.Dispose();
            throw;
        }

        _ = FreeSlotAsync(run, timeout, slot, request);

        _ = run.ContinueWith(
            ended => _busy.TryRemove(request.AgentId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return (run, started.Task);
    }

    /// <summary>
    /// Frees the slot when the run ends, or soon after its timeout if the cycle ignores its token. The runner then
    /// records the outcome but keeps waiting for the work, and the limit mustn't wait with it, or a few hung cycles
    /// would stop all the others. The agent keeps its place until the work really ends, because two cycles at once
    /// for one agent would be worse than a stalled one.
    /// </summary>
    private async Task FreeSlotAsync(Task run, TimeSpan timeout, CycleSlots.Slot slot, CycleRequest request)
    {
        await run.WaitAsync(timeout + HungGrace, time).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (!run.IsCompleted)
        {
            using var scope = BeginScope(request);

            logger.LogError(
                "The decision cycle for the candle that closed at {CandleClose} is still running past its timeout, so it no longer counts towards the limit",
                request.CandleClose);
        }

        slot.Dispose();
    }

    private async Task RunAsync(CycleRequest request, DateTimeOffset due, JobContext context, CancellationToken cancellationToken)
    {
        using var scope = BeginScope(request);

        // Counts and IDs only: administrators read it, and account deletion doesn't reach it.
        context.Details = $"Agent {request.AgentId}, candle {request.CandleClose:O}{(request.CatchUp ? ", catch-up" : "")}";

        var startDelay = time.GetUtcNow() - due;

        if (!request.CatchUp && startDelay > StartTarget)
        {
            logger.LogWarning(
                "The decision cycle for the candle that closed at {CandleClose} started {StartDelayMs:0} ms after it was due, later than the target",
                request.CandleClose,
                startDelay.TotalMilliseconds);
        }
        else
        {
            logger.LogInformation(
                "The decision cycle for the candle that closed at {CandleClose} started {StartDelayMs:0} ms after it was due, catch-up {CatchUp}",
                request.CandleClose,
                startDelay.TotalMilliseconds,
                request.CatchUp);
        }

        await cycles.RunAsync(request, context, cancellationToken);
    }

    private Task JitterAsync(CancellationToken stopping)
    {
        if (settings.MaxJitter <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var jitter = TimeSpan.FromTicks(Random.Shared.NextInt64(settings.MaxJitter.Ticks + 1));

        return Task.Delay(jitter, time, stopping);
    }

    private IDisposable? BeginScope(CycleRequest request) =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            [LogProperties.UserId] = request.UserId,
            [LogProperties.AgentId] = request.AgentId,
        });

    private enum DispatchOutcome
    {
        Started,
        Busy,
        PastDeadline,
        NotStarted,
    }
}

/// <summary>What happened to one close's cycles.</summary>
/// <param name="Busy">Skipped, because the agent's previous cycle was still running.</param>
/// <param name="PastDeadline">Dropped, because they were still queued at their deadline.</param>
/// <param name="NotStarted">Never started, because shutdown began or the run couldn't be recorded.</param>
internal sealed record DispatchTally(int Started, int Busy, int PastDeadline, int NotStarted);
