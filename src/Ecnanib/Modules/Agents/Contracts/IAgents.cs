namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// A user's trading agents (FR-AGT-01 to FR-AGT-05, FR-AGT-10, FR-AGT-12): their settings and their
/// lifecycle. Every call is scoped to the user, so another user's agent behaves like one that doesn't exist
/// (NFR-SEC-04). Every change of status is audited with who made it and why (FR-AGT-04). See Agents in
/// docs/architecture.md.
/// </summary>
public interface IAgents
{
    /// <summary>The most agents one user may have that aren't archived, from <c>Agents:MaxAgentsPerUser</c>.</summary>
    int MaxAgentsPerUser { get; }

    /// <summary>The user's agents, oldest first. Archived ones only when <paramref name="includeArchived"/> is set.</summary>
    Task<IReadOnlyList<AgentRecord>> ListAsync(Guid userId, bool includeArchived, CancellationToken cancellationToken);

    Task<AgentRecord?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a Draft. The pair is checked against Binance's symbols (FR-AGT-02), and the exchange key, when
    /// one is chosen, must be the user's Binance key for the agent's environment. Only Demo is accepted until
    /// switching to Live exists (#39).
    /// </summary>
    Task<AgentSaveResult> CreateAsync(Guid userId, AgentSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the agent's settings, if it's still at <paramref name="version"/>. The market, environment,
    /// pair, timeframe and exchange key can't change while the agent has open positions or orders
    /// (FR-AGT-03). An Active agent's new settings must still pass the activation checks. An archived agent
    /// can't be changed.
    /// </summary>
    Task<AgentSaveResult> UpdateAsync(Guid userId, Guid id, int version, AgentSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// A new Draft with the same settings and none of the history (FR-AGT-12). An archived agent can be cloned
    /// too. The caller names it, because the name is text the user reads.
    /// </summary>
    Task<AgentSaveResult> CloneAsync(Guid userId, Guid id, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Makes a Draft, Paused, Stopped or Error agent Active, if it passes the activation checks (FR-AGT-05),
    /// and no other agent holds its exchange key, market and pair (FR-AGT-10).
    /// </summary>
    Task<ActivationResult> ActivateAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>Stops new decision cycles of an Active agent. Its positions and protective orders stay (FR-AGT-06).</summary>
    Task<TransitionResult> PauseAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Ends an Active, Paused or Error agent's decision cycles, and leaves its positions and orders as they
    /// are. Closing them as it stops comes with the kill switch (FR-AGT-07, #37).
    /// </summary>
    Task<TransitionResult> StopAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Archives an agent that isn't Active and has no open positions or orders. This is how an agent is
    /// removed by default (DATA-R-04), and it frees the agent's name.
    /// </summary>
    Task<TransitionResult> ArchiveAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes an archived agent for good.</summary>
    Task<TransitionResult> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The agents that depend on one of the user's credentials, so that deleting it can say which will pause
    /// (FR-KEY-10). A Binance key is used by the agents that trade with it. An AI key is used by the agents
    /// with a required role on its provider, when it's the user's only valid key for that provider. Archived
    /// agents aren't listed.
    /// </summary>
    Task<IReadOnlyList<AgentUse>> ListUsingCredentialAsync(Guid userId, Guid credentialId, CancellationToken cancellationToken);
}

/// <param name="WillPause">It's Active, so it will pause when the credential goes.</param>
public sealed record AgentUse(Guid Id, string Name, AgentStatus Status, bool WillPause);
