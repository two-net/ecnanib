using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Execution's side of the kill switch (FR-AGT-08), for Agents (see Inversions in docs/architecture.md). It holds the
/// agent's lock, so it never acts at the same time as a cycle's execution, the watch or a sweep, and reconciles the
/// agent: for an agent the kill switch stopped, that cancels its unfilled entries and closes every part it holds at
/// market once Binance has confirmed its stops, each close under an evaluation of its own (AC-01). Then it passes until
/// nothing is urgent, and leaves the rest to the watch, and to the reconcile sweep, which does the same for as long as
/// anything is left. Only the agent's own orders are touched, because only its records are (FR-AGT-09). See The kill
/// switch in docs/architecture.md.
/// </summary>
internal sealed class AgentLiquidation(
    AgentLocks locks,
    AgentSessions sessions,
    OrderStore store,
    ProtectionKeeper keeper,
    Reconciler reconciler,
    OrderWatch watch,
    ILogger<AgentLiquidation> logger) : IAgentLiquidation
{
    public async Task<LiquidationOutcome> CloseAllAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { [LogProperties.AgentId] = agentId });
        using var held = await locks.HoldAsync(agentId, cancellationToken);

        // Every order is written before it's sent, so the records know everything the agent has on Binance.
        var latest = await store.FindLatestEntryAsync(userId, agentId, cancellationToken);

        if (latest is null || !await store.HasOpenPositionsOrOrdersAsync(userId, agentId, cancellationToken))
        {
            logger.LogInformation("The agent has nothing open on Binance to close");
            return LiquidationOutcome.NothingOpen;
        }

        AgentSession session;

        try
        {
            session = await sessions.OpenAsync(userId, agentId, latest.CredentialId, latest.Market, latest.Environment, latest.Symbol, cancellationToken);
        }
        catch (AgentSessionException exception)
        {
            // Nothing can be sent without the key. Its stops stay on Binance, and the sweep tries again.
            logger.LogError("The agent's positions can't be closed: {Failure}", exception.Failure);
            return LiquidationOutcome.Unconfirmed;
        }

        using (session)
        {
            if (!session.Liquidating)
            {
                // Activated again before this could take the lock, so it trades again and closes nothing.
                logger.LogWarning("The agent is {Status} again, so nothing is closed", session.Agent?.Status);
                return LiquidationOutcome.Unconfirmed;
            }

            var reconciled = await reconciler.ReconcileAsync(session, readPosition: false, cancellationToken);
            var pass = await keeper.KeepAsync(session, OrderExecutor.MaximumWait, OrderExecutor.UrgentPassDelay, cancellationToken);

            // Under the lock, so a watch that has just found nothing to do can't miss it.
            if (pass.HasWork)
            {
                watch.Ensure(userId, agentId);
            }

            if (!await store.HasOpenPositionsOrOrdersAsync(userId, agentId, cancellationToken))
            {
                logger.LogWarning("The kill switch closed everything the agent had open");
                return LiquidationOutcome.Closed;
            }

            // A part still resting is one whose stop Binance couldn't confirm, so it wasn't closed yet.
            if (!reconciled.Verified && pass.Orders.Protections.Any(protection => protection.Status == ProtectionStatus.Resting))
            {
                logger.LogWarning("Binance couldn't confirm the agent's orders, so its positions are closed once it can");
                return LiquidationOutcome.Unconfirmed;
            }

            logger.LogWarning("The kill switch is still closing what the agent had open, which execution finishes");
            return LiquidationOutcome.Closing;
        }
    }
}
