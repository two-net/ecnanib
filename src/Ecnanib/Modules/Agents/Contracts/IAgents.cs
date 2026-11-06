using Ecnanib.Modules.RiskEngine.Contracts;

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

    /// <summary>
    /// <c>Security:RequireTwoFactorForLive</c>: whether switching an agent to Live needs two-factor turned on, so a
    /// page can say so before the user tries (FR-AGT-05).
    /// </summary>
    bool TwoFactorRequiredForLive { get; }

    /// <summary>The user's agents, oldest first. Archived ones only when <paramref name="includeArchived"/> is set.</summary>
    Task<IReadOnlyList<AgentRecord>> ListAsync(Guid userId, bool includeArchived, CancellationToken cancellationToken);

    Task<AgentRecord?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The models the user's valid AI keys can give each role, and the assignment a new agent starts with,
    /// from the default role assignments (FR-ROLE-02 to FR-ROLE-05). Only the user's own keys (NFR-SEC-04).
    /// </summary>
    Task<RoleChoices> ListRoleChoicesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a Draft. The pair is checked against Binance's symbols (FR-AGT-02), and the exchange key, when
    /// one is chosen, must be the user's Binance key for the agent's environment. Every agent starts in Demo,
    /// and <see cref="SwitchToLiveAsync"/> is the only way to Live (UC-04).
    /// </summary>
    Task<AgentSaveResult> CreateAsync(Guid userId, AgentSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the agent's settings, if it's still at <paramref name="version"/>. The market, environment,
    /// pair, timeframe and exchange key can't change while the agent has open positions or orders
    /// (FR-AGT-03). The environment never changes by an edit: <see cref="SwitchToLiveAsync"/> does that. An
    /// Active agent's new settings must still pass the activation checks. An archived agent can't be changed.
    /// </summary>
    Task<AgentSaveResult> UpdateAsync(Guid userId, Guid id, int version, AgentSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// A new Draft with the same settings and none of the history (FR-AGT-12). An archived agent can be cloned
    /// too. The caller names it, because the name is text the user reads. A Live agent's clone is a Demo Draft
    /// with no exchange key, because only <see cref="SwitchToLiveAsync"/> makes an agent Live.
    /// </summary>
    Task<AgentSaveResult> CloneAsync(Guid userId, Guid id, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Makes a Draft, Paused, Stopped or Error agent Active, if it passes the activation checks (FR-AGT-05),
    /// and no other agent holds its exchange key, market and pair (FR-AGT-10).
    /// </summary>
    Task<ActivationResult> ActivateAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Switches a Paused or Stopped Demo agent with no open positions or orders to Live, with one of the user's Binance
    /// Live keys, and makes it Active (UC-04). On top of the activation checks, run against the Live key, pair and
    /// account (FR-AGT-05), it needs a verified address when email is on, two-factor turned on when
    /// <see cref="TwoFactorRequiredForLive"/>, and the user's acknowledgement of the Risk Disclosure and the execution
    /// mode. The code is checked last, once nothing else refuses, so a refusal doesn't use it. It's audited as
    /// <c>AgentSwitchedToLive</c> and <c>AgentActivated</c>. There's no way back to Demo: clone the agent for that.
    /// </summary>
    Task<LiveSwitchResult> SwitchToLiveAsync(Guid userId, Guid id, LiveSwitchRequest request, CancellationToken cancellationToken);

    /// <summary>Stops new decision cycles of an Active agent. Its positions and protective orders stay (FR-AGT-06).</summary>
    Task<TransitionResult> PauseAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Pauses an Active agent for a loss limit the Risk Engine found it reached (FR-RISK-08), by the system, and
    /// tells its user, in one transaction. A daily or weekly limit's pause lasts until <see cref="LossLimitBreach.Until"/>:
    /// the agent can't be activated before then, and resumes on its own after. The consecutive-loss count's lasts
    /// until the user resumes it. The decision cycle calls it with the breach from
    /// <see cref="IRiskEngine.CheckLossLimits"/> or an entry's evaluation.
    /// </summary>
    /// <returns>False when the agent isn't Active, or the period has already ended, so nothing was done.</returns>
    Task<bool> PauseForLossLimitAsync(Guid userId, Guid id, LossLimitBreach breach, CancellationToken cancellationToken);

    /// <summary>
    /// Puts an Active agent in Error, by the system, because its decision cycles kept failing to reach a decision,
    /// and tells its user, in one transaction (FR-AGT-04, FR-NTF-01). Its positions and protective orders stay on
    /// the exchange. The user activates it again once they've fixed what was wrong. The decision cycle calls it.
    /// </summary>
    /// <returns>False when the agent isn't Active, so nothing was done.</returns>
    Task<bool> EnterErrorAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Ends an Active, Paused or Error agent's decision cycles, and leaves its positions and orders as they
    /// are, for the user to manage (FR-AGT-07). <see cref="StopAndCloseAsync"/> is the other choice.
    /// </summary>
    Task<TransitionResult> StopAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The agent's kill switch, which is also FR-AGT-07's choice to close as it stops (FR-AGT-08): it stops the agent,
    /// then cancels the open orders it created and closes its positions at market, through
    /// <see cref="IAgentLiquidation"/>. It needs a code when the user has two-factor on (FR-AUTH-05), and changes
    /// nothing without one. An Active, Paused, Error or Stopped agent can be killed, a Stopped one for what it still
    /// holds. It's audited, and raises <c>KillSwitchExecuted</c>. It returns once nothing is urgent, and whatever is
    /// left, execution finishes on its own.
    /// </summary>
    /// <param name="twoFactorCode">A code from the user's authenticator, or a recovery code. Ignored when two-factor is off.</param>
    Task<KillResult> StopAndCloseAsync(Guid userId, Guid id, string? twoFactorCode, CancellationToken cancellationToken);

    /// <summary>
    /// "Stop all agents" (FR-AGT-08, UC-05): the kill switch for each of the user's agents that's Active, Paused or in
    /// Error, or Stopped with something still open, one at a time, so one that fails doesn't keep the rest from
    /// stopping. The code is checked once, before any of them.
    /// </summary>
    /// <param name="twoFactorCode">A code from the user's authenticator, or a recovery code. Ignored when two-factor is off.</param>
    Task<StopAllResult> StopAllAsync(Guid userId, string? twoFactorCode, CancellationToken cancellationToken);

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
