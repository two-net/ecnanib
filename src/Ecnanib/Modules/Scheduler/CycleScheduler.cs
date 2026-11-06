using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Scheduler;

/// <summary>
/// What each candle-close job does: decides whether the close is on time, a catch-up or too late (FR-CYC-04), and
/// hands every Active agent of its timeframe to <see cref="CycleDispatcher"/> with the cycle's deadline (FR-CYC-06).
/// See Scheduler in docs/architecture.md.
/// </summary>
internal sealed class CycleScheduler(
    IActiveAgents agents,
    CycleDispatcher dispatcher,
    SchedulerSettings settings,
    TimeProvider time,
    ILogger<CycleScheduler> logger)
{
    /// <summary>
    /// A run this late is still on time. A cycle should start within 30 seconds of its close and the delay
    /// (NFR-PERF-01), so a later run means the application, or its database, wasn't there at the close.
    /// </summary>
    public static readonly TimeSpan OnTime = CycleDispatcher.StartTarget;

    public JobSchedule Schedule(Timeframe timeframe) => new CandleSchedule(timeframe, settings.CandleCloseDelay);

    /// <summary>Covers the jitter and the wait for a slot, which gives up at the deadline, with a minute to spare.</summary>
    public TimeSpan Timeout(Timeframe timeframe) => settings.CycleDeadline(timeframe) + settings.MaxJitter + TimeSpan.FromMinutes(1);

    /// <summary>
    /// Runs the latest close's cycles. Only the latest: when the application was down for several closes, the
    /// earlier ones are gone, so an agent gets at most one catch-up (FR-CYC-04).
    /// </summary>
    public async Task RunAsync(Timeframe timeframe, JobContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var close = CandleCloses.LatestAtOrBefore(timeframe, now - settings.CandleCloseDelay);
        var due = close + settings.CandleCloseDelay;
        var late = now - due;
        var grace = settings.CatchUpGrace(timeframe);
        var catchUp = late > OnTime;

        var active = await agents.ListAsync(timeframe, cancellationToken);

        if (catchUp && late > grace)
        {
            foreach (var agent in active)
            {
                using var scope = BeginScope(agent);

                logger.LogWarning(
                    "Skipped the decision cycle for the candle that closed at {CandleClose}: it's {LateMinutes:0.0} min late, past the catch-up grace of {CatchUpGraceMinutes} min",
                    close,
                    late.TotalMinutes,
                    grace.TotalMinutes);
            }

            context.Details = $"Candle {close:O}, {active.Count} skipped as too late";

            return;
        }

        // A catch-up's deadline counts from now, or its grace would be no use. Otherwise time in the queue counts.
        var deadline = (catchUp ? now : due) + settings.CycleDeadline(timeframe);

        var requests = active
            .Select(agent => new CycleRequest(agent.UserId, agent.Id, timeframe, close, deadline, catchUp))
            .ToArray();

        // So the order of the rows doesn't decide who goes first when the limit is reached.
        Random.Shared.Shuffle(requests);

        var tally = await dispatcher.DispatchAsync(requests, due, context.Stopping);

        context.Details =
            $"Candle {close:O}{(catchUp ? ", catch-up" : "")}, due {requests.Length}, started {tally.Started}, "
            + $"still running {tally.Busy}, past deadline {tally.PastDeadline}, not started {tally.NotStarted}";

        // Whatever hadn't started when shutdown began never will, so the run didn't finish.
        context.Stopping.ThrowIfCancellationRequested();
    }

    private IDisposable? BeginScope(ActiveAgent agent) =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            [LogProperties.UserId] = agent.UserId,
            [LogProperties.AgentId] = agent.Id,
        });
}
