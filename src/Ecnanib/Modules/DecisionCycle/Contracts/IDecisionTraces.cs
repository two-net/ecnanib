namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// A user's decision traces. Every call takes the user, so another user's agent behaves like one with no cycles
/// (NFR-SEC-04). The trace viewer (#42) adds the rest of the trace. See Decision cycle in docs/architecture.md.
/// </summary>
public interface IDecisionTraces
{
    /// <summary>The agent's latest cycle, running or not, or null when it has none (FR-ROLE-08).</summary>
    Task<CycleProgress?> FindLatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}
