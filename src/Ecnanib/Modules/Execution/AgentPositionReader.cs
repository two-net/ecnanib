using Ecnanib.Modules.Execution.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// An agent's position from Execution's records alone, as <see cref="AgentAccounts"/> builds it after reconciling,
/// but with no exchange call and no lock (FR-AGT-11). See The agent's page in docs/architecture.md.
/// </summary>
internal sealed class AgentPositionReader(OrderStore orders) : IAgentPositions
{
    public async Task<RecordedPosition> FindAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        var records = await orders.LoadAsync(userId, agentId, cancellationToken);
        var held = AgentPositions.Of(records, positions: null);
        var pending = AgentPositions.PendingEntry(records);

        if (held is null && pending is null)
        {
            return RecordedPosition.None;
        }

        // The orders' own market, environment and pair: the agent's may have changed since, or be about to (FR-EXP-08).
        var entry = held is not null
            ? records.Entries.Last(entry => records.ProtectionsOf(entry).Any(part => OrderStore.HeldProtections.Contains(part.Status)))
            : records.Entries.Last(entry => OrderStore.PendingEntryStatuses.Contains(entry.Status));

        return new RecordedPosition(
            held?.Position,
            held?.OpenedAt,
            held?.Targets ?? [],
            pending,
            entry.Market,
            entry.Environment,
            entry.Symbol);
    }
}
