using System.Collections.Concurrent;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Watches an agent's orders until nothing is left to do: an unfilled entry is checked for fills every
/// <see cref="ExecutionSettings.PendingEntryCheck"/> and cancelled at its time-to-live (FR-EXE-01), each fill is
/// protected (FR-EXE-02, AC-02), and orders whose outcome isn't known are settled (FR-EXE-05). One run per agent at a
/// time, each a recorded job run with its own correlation ID (NFR-REL-05). The sweep starts any that should be running,
/// and so does reconciliation, including after a restart. See Order execution in docs/architecture.md.
/// </summary>
internal sealed class OrderWatch(
    IJobRunner runner,
    AgentLocks locks,
    AgentSessions sessions,
    OrderStore store,
    ProtectionKeeper keeper,
    ExecutionSettings settings,
    TimeProvider time,
    ILogger<OrderWatch> logger)
{
    public const string JobName = "order-watch";

    /// <summary>A run hands over to a new one after this, so no run lasts as long as a limit entry can.</summary>
    public static readonly TimeSpan RunLength = TimeSpan.FromHours(1);

    /// <summary>Beyond <see cref="RunLength"/>, for a pass under way to finish.</summary>
    private static readonly TimeSpan WindDown = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, object> _watching = new();

    /// <summary>Whether a run is watching the agent now.</summary>
    public bool IsWatching(Guid agentId) => _watching.ContainsKey(agentId);

    /// <summary>
    /// Starts the agent's watch unless one is running. Call it while holding the agent's lock, so a run that's just
    /// found nothing to do can't miss what the caller left.
    /// </summary>
    public void Ensure(Guid userId, Guid agentId)
    {
        var token = new object();

        if (!_watching.TryAdd(agentId, token))
        {
            return;
        }

        var more = new HandOver();
        var run = runner.RunAsync(JobName, RunLength + WindDown, (context, cancellationToken) =>
            WatchAsync(userId, agentId, token, more, context, cancellationToken));

        _ = run.ContinueWith(
            _ =>
            {
                Release(agentId, token);

                if (more.Due)
                {
                    Ensure(userId, agentId);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WatchAsync(
        Guid userId, Guid agentId, object token, HandOver more, JobContext context, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { [LogProperties.AgentId] = agentId });

        var until = time.GetUtcNow() + RunLength;
        var passes = 0;

        while (!context.Stopping.IsCancellationRequested)
        {
            KeeperPass pass;

            using (await locks.HoldAsync(agentId, cancellationToken))
            {
                var latest = await store.FindLatestEntryAsync(userId, agentId, cancellationToken);

                if (latest is null)
                {
                    Release(agentId, token);
                    return;
                }

                AgentSession session;

                try
                {
                    session = await sessions.OpenAsync(
                        userId, agentId, latest.CredentialId, latest.Market, latest.Environment, latest.Symbol, cancellationToken);
                }
                catch (AgentSessionException exception)
                {
                    // Nothing can be sent without the key. The sweep tries again in a minute.
                    logger.LogError("The agent's orders can't be looked after: {Failure}", exception.Failure);
                    context.Details = $"Failure: {exception.Failure}";
                    return;
                }

                using (session)
                {
                    pass = await keeper.AdvanceAsync(session, cancellationToken);
                }

                passes++;

                // Under the lock, so whoever holds it next finds no watch and starts one.
                if (!pass.HasWork)
                {
                    Release(agentId, token);
                    context.Details = $"Passes: {passes}";
                    return;
                }
            }

            if (time.GetUtcNow() >= until)
            {
                more.Due = true;
                context.Details = $"Passes: {passes}, handed over";
                return;
            }

            try
            {
                await Task.Delay(pass.Urgent ? OrderExecutor.UrgentPassDelay : settings.PendingEntryCheck, time, context.Stopping);
            }
            catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested)
            {
                break;
            }
        }

        // Shutdown. A pass under way finished, because exchange calls get the run's token. The sweep resumes the rest at
        // the next start.
        context.Details = $"Passes: {passes}";
        context.Stopping.ThrowIfCancellationRequested();
    }

    private void Release(Guid agentId, object token) => _watching.TryRemove(new KeyValuePair<Guid, object>(agentId, token));

    private sealed class HandOver
    {
        public bool Due { get; set; }
    }
}
