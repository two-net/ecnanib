namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The per-agent gate that serialises evaluations and lifecycle actions, for a module that must change an agent's
/// orders or trades without an evaluation in flight (the execution module's reconciliation, FR-EXE-08).
/// </summary>
public interface IAgentGates
{
    Task<IDisposable> LockAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>Takes the gate only when it is free; null when an evaluation or an action holds it.</summary>
    IDisposable? TryLock(Guid agentId);
}
