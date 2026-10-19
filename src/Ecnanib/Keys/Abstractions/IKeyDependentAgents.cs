namespace Ecnanib.Keys.Abstractions;

/// <summary>
/// How the keys module acts on agents that use a key. The default implementation writes the agent rows (Appendix E
/// transitions with a reason); the agents runtime replaces it so that Stop also cancels orders and the Error
/// transition halts evaluation (FR-AGT-06). Only Running and Paused agents are affected; the return value counts them.
/// </summary>
public interface IKeyDependentAgents
{
    /// <summary>Moves the agents to Stopped because a key they use is being deleted (FR-KEY-06).</summary>
    Task<int> StopAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken);

    /// <summary>Moves the agents to Error because a key they use is invalid (FR-KEY-07); they open nothing new until the user fixes the key and starts them.</summary>
    Task<int> FailAsync(IReadOnlyList<Guid> agentIds, string reason, CancellationToken cancellationToken);
}
