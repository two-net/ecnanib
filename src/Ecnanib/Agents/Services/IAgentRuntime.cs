using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Services;

/// <summary>What the scheduler needs to evaluate an agent: whose it is, which candle stream to follow and which strategy's series the loader must hold.</summary>
internal sealed record AgentTrackingInfo(Guid AgentId, Guid UserId, MarketKey Key, CandleInterval Timeframe, string StrategyId, int StrategyVersion);

/// <summary>
/// The in-process side of the lifecycle (FR-AGT-08/09): which agents are followed, a per-agent gate that serialises
/// evaluations and lifecycle actions, and the Error transition that halts evaluation. One instance per process (C-10).
/// </summary>
internal interface IAgentRuntime
{
    /// <summary>Takes the agent's gate; evaluations run under the same gate, so the caller may change state without one in flight.</summary>
    Task<IDisposable> LockAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>Cancels the evaluation in flight, if any, up to its execution stage; the gate still waits for it to finish.</summary>
    void CancelEvaluation(Guid agentId);

    bool IsTracked(Guid agentId);

    /// <summary>Follows the agent's candle stream; false when the host is shutting down (the agent is resumed at the next start, NFR-REL-01).</summary>
    bool Track(AgentTrackingInfo info);

    void Untrack(Guid agentId);

    /// <summary>Drops the agent's gate after deletion.</summary>
    void Forget(Guid agentId);

    /// <summary>Running or Paused → Error with the reason, and untracked (Appendix E "unrecoverable failure"). The caller holds the gate. Returns whether a row changed.</summary>
    Task<bool> FailAsync(Guid agentId, string reason, CancellationToken cancellationToken);
}

/// <summary>What the pipeline asks of the scheduler while a run is in progress.</summary>
internal interface IEvaluationHost
{
    bool IsTracked(Guid agentId);

    Task<bool> FailAsync(Guid agentId, string reason, CancellationToken cancellationToken);
}
