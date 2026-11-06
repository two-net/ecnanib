using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// A port Agents declares for the modules above it that keep an agent's history, such as its decision cycles. Agents
/// can't reach their tables, so deleting an agent for good asks each owner to delete its rows, in the delete's own
/// transaction, so nothing is left behind (see Inversions in docs/architecture.md). Register an implementation in
/// the owner's <c>Add&lt;Module&gt;()</c>.
/// </summary>
public interface IAgentHistory
{
    /// <summary>Deletes the agent's rows on <paramref name="context"/>, whose transaction the caller commits.</summary>
    Task DeleteAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken);
}
