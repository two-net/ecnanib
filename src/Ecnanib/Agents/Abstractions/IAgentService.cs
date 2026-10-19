namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The user's agents (FR-AGT-01..07, FR-AGT-15): creation, the editable sections with their change history, and the
/// Appendix E actions. Every operation is scoped to <c>userId</c> (NFR-SEC-03); another user's agent answers not
/// found. User mistakes are results; exceptions mean the database or the exchange failed.
/// </summary>
public interface IAgentService
{
    Task<IReadOnlyList<AgentSummary>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<AgentDetail?> GetAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>Creates a Draft with the timeframe's default strategy, the Appendix B council and default risk limits (FR-AGT-01, FR-AGT-15).</summary>
    Task<AgentSaveResult> CreateAsync(Guid userId, AgentCreateInput input, CancellationToken cancellationToken);

    Task<AgentSaveResult> UpdateSettingsAsync(Guid userId, Guid agentId, AgentSettingsInput input, CancellationToken cancellationToken);

    Task<AgentSaveResult> UpdateStrategyAsync(Guid userId, Guid agentId, StrategyInput input, CancellationToken cancellationToken);

    Task<AgentSaveResult> UpdateCouncilAsync(Guid userId, Guid agentId, CouncilInput input, CancellationToken cancellationToken);

    Task<AgentSaveResult> UpdateRiskLimitsAsync(Guid userId, Guid agentId, RiskLimitsInput input, CancellationToken cancellationToken);

    /// <summary>A new Draft with the same configuration; counts against the agents-per-user limit (FR-AGT-04, FR-AGT-15).</summary>
    Task<AgentActionResult> CloneAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>Draft or Stopped only (Appendix E); orders, trades and history go with it.</summary>
    Task<AgentActionResult> DeleteAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>Draft, Stopped or Error → Running after the limit, strategy, key, disclaimer and council checks (FR-AGT-15, FR-ACC-08, FR-KEY-08).</summary>
    Task<AgentActionResult> StartAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>Running → Paused: still evaluated, opens nothing new (FR-AGT-05).</summary>
    Task<AgentActionResult> PauseAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    Task<AgentActionResult> ResumeAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>Cancels open orders, then closes positions at market or leaves them flagged as unmanaged (FR-AGT-06).</summary>
    Task<AgentActionResult> StopAsync(Guid userId, Guid agentId, bool closePositions, CancellationToken cancellationToken);
}
