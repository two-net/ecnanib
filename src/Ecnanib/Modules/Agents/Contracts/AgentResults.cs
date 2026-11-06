using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Agents.Contracts;

public enum AgentSaveOutcome
{
    /// <summary><see cref="AgentSaveResult.Agent"/> is the agent as it's stored now.</summary>
    Saved,

    /// <summary><see cref="AgentSaveResult.Fields"/> and <see cref="AgentSaveResult.RiskSettings"/> say what's wrong. Nothing was asked of Binance.</summary>
    Invalid,

    /// <summary>Another of the user's agents that isn't archived has the name, ignoring case.</summary>
    NameTaken,

    /// <summary>The user already has <see cref="IAgents.MaxAgentsPerUser"/> agents that aren't archived.</summary>
    LimitReached,

    /// <summary>Binance doesn't list the pair on the agent's market and environment.</summary>
    SymbolUnknown,

    /// <summary>Binance lists the pair, but it isn't trading now.</summary>
    SymbolNotTrading,

    /// <summary>A Futures pair that isn't a perpetual contract, which has no funding and expires.</summary>
    SymbolNotPerpetual,

    /// <summary>Binance can't be reached, and its symbols aren't stored yet. Try again later.</summary>
    SymbolUnverifiable,

    /// <summary>
    /// A new agent that isn't Demo, or an edit that changes the environment. Every agent starts in Demo, and becomes
    /// Live only through <see cref="IAgents.SwitchToLiveAsync"/> (UC-04).
    /// </summary>
    EnvironmentNotEditable,

    /// <summary>The market, environment, pair, timeframe or exchange key changed while the agent has open positions or orders (FR-AGT-03).</summary>
    LockedByOpenPositions,

    /// <summary>The agent is Active, and the new settings fail the activation checks in <see cref="AgentSaveResult.Problems"/>.</summary>
    WouldFailActivation,

    /// <summary>
    /// The agent is Active, Paused or in Error, and another such agent already trades the new pair on the
    /// same market with the same exchange key (FR-AGT-10). <see cref="AgentSaveResult.Problems"/> names it.
    /// </summary>
    SlotTaken,

    /// <summary>An archived agent can't be changed.</summary>
    Archived,

    NotFound,

    /// <summary>The agent changed since the version the edit started from, so this edit wasn't saved.</summary>
    Changed,
}

/// <summary>A setting <see cref="IAgents.CreateAsync"/> or <see cref="IAgents.UpdateAsync"/> refused.</summary>
public enum AgentField
{
    Name,
    Description,
    Market,
    Environment,
    Symbol,
    Timeframe,

    /// <summary>Not one of the user's Binance keys, or not for the agent's environment.</summary>
    ExchangeCredential,

    /// <summary>Two assignments for one role, or a model with no provider or ID.</summary>
    Roles,

    ExecutionMode,

    CapitalAllocation,

    /// <summary><see cref="AgentSaveResult.RiskSettings"/> says which.</summary>
    Risk,

    /// <summary>A limit that isn't positive, or a month's below a day's.</summary>
    Budget,

    ReflectionsInPrompt,
    CustomInstructions,
}

/// <param name="Agent">The agent as it's stored, when saved, and as it is now when <see cref="AgentSaveOutcome.Changed"/>.</param>
/// <param name="Fields">What's wrong, when <see cref="AgentSaveOutcome.Invalid"/>.</param>
/// <param name="RiskSettings">
/// Which risk settings are outside their range or beyond the global caps (FR-RISK-11), when <see cref="Fields"/>
/// has <see cref="AgentField.Risk"/>.
/// </param>
/// <param name="Problems">Why the Active agent's new settings fail activation, when <see cref="AgentSaveOutcome.WouldFailActivation"/>.</param>
public sealed record AgentSaveResult(
    AgentSaveOutcome Outcome,
    AgentRecord? Agent = null,
    IReadOnlyList<AgentField>? Fields = null,
    IReadOnlyList<RiskSetting>? RiskSettings = null,
    IReadOnlyList<ActivationProblem>? Problems = null);

public enum TransitionOutcome
{
    Done,

    /// <summary>The agent's status doesn't allow it. <see cref="TransitionResult.Agent"/> has the status it's in.</summary>
    NotAllowed,

    /// <summary>The agent has open positions or orders, so it can't be archived.</summary>
    OpenPositions,

    NotFound,
}

/// <param name="Agent">The agent as it is now, unless <see cref="TransitionOutcome.NotFound"/> or deleted.</param>
public sealed record TransitionResult(TransitionOutcome Outcome, AgentRecord? Agent = null);

/// <summary>What the kill switch did to one agent (FR-AGT-08, UC-05).</summary>
public enum KillOutcome
{
    /// <summary>Stopped, and it had no open orders or positions.</summary>
    NothingOpen,

    /// <summary>Stopped, its open orders cancelled and its positions closed.</summary>
    Closed,

    /// <summary>Stopped, and closing is under way: Ecnanib finishes it on its own, and notifies each trade it closes.</summary>
    Closing,

    /// <summary>
    /// Stopped, but Binance couldn't be reached or the key can't be used, so nothing was closed yet. Its stops stay on
    /// Binance, and Ecnanib keeps trying.
    /// </summary>
    Unconfirmed,

    /// <summary>A Draft or Archived agent, which can have nothing on the exchange. <see cref="KillResult.Agent"/> has its status.</summary>
    NotAllowed,

    NotFound,

    /// <summary>The two-factor code was missing or wrong (FR-AUTH-05). Nothing changed. <see cref="KillResult.TwoFactor"/> says why.</summary>
    TwoFactorRefused,

    /// <summary>Something failed before the agent could be stopped, so it's as it was. "Stop all agents" went on with the rest.</summary>
    Failed,
}

/// <param name="Agent">The agent as it is now, unless <see cref="KillOutcome.NotFound"/> or <see cref="KillOutcome.TwoFactorRefused"/>.</param>
/// <param name="TwoFactor">The check that refused, when <see cref="KillOutcome.TwoFactorRefused"/>.</param>
public sealed record KillResult(KillOutcome Outcome, AgentRecord? Agent = null, TwoFactorCheck? TwoFactor = null)
{
    /// <summary>Whether the agent is now stopped by the kill switch, whatever is still left to close.</summary>
    public bool Stopped => Outcome is KillOutcome.NothingOpen or KillOutcome.Closed or KillOutcome.Closing or KillOutcome.Unconfirmed;
}

/// <summary>One agent's part in "Stop all agents".</summary>
/// <param name="Name">The agent's name as it was read, for the report, even if the agent changed meanwhile.</param>
public sealed record AgentKill(Guid Id, string Name, KillResult Result);

/// <summary>What "Stop all agents" did (UC-05).</summary>
/// <param name="TwoFactor">The check that refused, in which case nothing changed and <paramref name="Agents"/> is empty.</param>
/// <param name="Agents">Each agent it went through, oldest first. Empty when the user had none to stop.</param>
public sealed record StopAllResult(TwoFactorCheck? TwoFactor, IReadOnlyList<AgentKill> Agents)
{
    public bool Refused => TwoFactor is { } check && !check.Allows();
}

public enum ActivationOutcome
{
    Activated,

    /// <summary><see cref="ActivationResult.Problems"/> says what has to be fixed first.</summary>
    Refused,

    /// <summary>The agent is Active or Archived already.</summary>
    NotAllowed,

    NotFound,

    /// <summary>The agent changed while it was checked, so it wasn't activated. Check it again.</summary>
    Changed,
}

/// <param name="Agent">The agent as it is now, unless <see cref="ActivationOutcome.NotFound"/>.</param>
/// <param name="Warnings">Things worth knowing that don't stop activation.</param>
public sealed record ActivationResult(
    ActivationOutcome Outcome,
    AgentRecord? Agent = null,
    IReadOnlyList<ActivationProblem>? Problems = null,
    IReadOnlyList<ActivationWarning>? Warnings = null);

/// <summary>Something that stops an agent being activated, or switched to Live (FR-AGT-05, FR-AGT-10).</summary>
public enum ActivationProblemKind
{
    /// <summary>No exchange key is chosen.</summary>
    NoExchangeKey,

    /// <summary>The chosen exchange key was deleted.</summary>
    ExchangeKeyMissing,

    /// <summary>The exchange key isn't valid.</summary>
    ExchangeKeyNotValid,

    /// <summary>Binance doesn't allow the exchange key to trade on the agent's market.</summary>
    MarketNotEnabled,

    SymbolUnknown,
    SymbolNotTrading,
    SymbolNotPerpetual,
    SymbolUnverifiable,

    /// <summary>A required role has no model. <see cref="ActivationProblem.Role"/> says which.</summary>
    RoleUnassigned,

    /// <summary>A required role's model isn't Active in the catalog.</summary>
    ModelNotActive,

    /// <summary>None of the user's valid AI keys can use a required role's model.</summary>
    ModelNotAccessible,

    /// <summary>A required role's model can't be held to a JSON schema, which every role needs (FR-ROLE-02).</summary>
    ModelLacksStructuredOutput,

    NoCapitalAllocation,

    /// <summary>A risk setting is outside its range.</summary>
    RiskSettingsInvalid,

    /// <summary>
    /// A risk setting is beyond the global hard caps in configuration (FR-RISK-11), which may have been tightened
    /// since the agent was saved.
    /// </summary>
    RiskSettingsAboveCaps,

    /// <summary>
    /// A daily or weekly loss limit paused the agent, and the pause hasn't ended (FR-RISK-08).
    /// <see cref="ActivationProblem.Until"/> says when it does.
    /// </summary>
    LossLimitPause,

    /// <summary>
    /// Another agent that's Active, Paused or in Error trades the same pair on the same market with the same
    /// exchange key (FR-AGT-10). <see cref="ActivationProblem.OtherAgent"/> names it.
    /// </summary>
    SlotTaken,

    /// <summary>Binance couldn't be reached to check the Futures account (EXT-BIN-07). Trying again later may work.</summary>
    FuturesAccountUnverifiable,

    /// <summary>The Futures account is in Hedge Mode, and agents need One-way Mode (EXT-BIN-07, FR-RISK-09).</summary>
    FuturesHedgeMode,

    /// <summary>
    /// The Futures account is in Multi-Assets Mode, which allows cross margin only, so the pair can't be set to
    /// isolated (FR-RISK-09).
    /// </summary>
    FuturesMultiAssetsMode,

    /// <summary>
    /// The Futures account has less margin available than the smallest position Binance accepts for the pair needs at
    /// the agent's leverage (EXT-BIN-07). <see cref="ActivationProblem.Asset"/>, <see cref="ActivationProblem.Required"/>
    /// and <see cref="ActivationProblem.Available"/> say how much.
    /// </summary>
    FuturesMarginInsufficient,

    /// <summary>Email is on, and the user hasn't verified their address, which switching to Live needs (FR-AUTH-03).</summary>
    EmailNotVerified,

    /// <summary>
    /// <c>Security:RequireTwoFactorForLive</c> is on, and the user hasn't turned on two-factor authentication, which
    /// switching to Live then needs (FR-AGT-05).
    /// </summary>
    TwoFactorRequired,

    /// <summary>The user didn't acknowledge the Risk Disclosure when switching to Live (FR-AGT-05).</summary>
    RiskDisclosureNotAcknowledged,

    /// <summary>The user didn't acknowledge the agent's execution mode when switching to Live (FR-AGT-05).</summary>
    ExecutionModeNotAcknowledged,
}

/// <param name="Role">Which role, for the role problems.</param>
/// <param name="OtherAgent">The other agent's name, for <see cref="ActivationProblemKind.SlotTaken"/>. Only show it as text.</param>
/// <param name="Until">When the pause ends, for <see cref="ActivationProblemKind.LossLimitPause"/>.</param>
/// <param name="Asset">The margin asset, for <see cref="ActivationProblemKind.FuturesMarginInsufficient"/>.</param>
/// <param name="Required">The margin the smallest position needs, in <paramref name="Asset"/>.</param>
/// <param name="Available">The margin the account has available, in <paramref name="Asset"/>.</param>
public sealed record ActivationProblem(
    ActivationProblemKind Kind,
    AiRole? Role = null,
    string? OtherAgent = null,
    DateTimeOffset? Until = null,
    string? Asset = null,
    decimal? Required = null,
    decimal? Available = null);

public enum ActivationWarningKind
{
    /// <summary>An optional role's model isn't Active, or none of the user's valid keys can use it, so the role will be skipped.</summary>
    OptionalModelUnavailable,

    /// <summary>A role's fallback model isn't Active, or none of the user's valid keys can use it.</summary>
    FallbackUnavailable,

    /// <summary>An optional role's model can't be held to a JSON schema (FR-ROLE-02), so the role will be skipped.</summary>
    OptionalModelLacksStructuredOutput,

    /// <summary>A role's fallback model can't be held to a JSON schema (FR-ROLE-02).</summary>
    FallbackLacksStructuredOutput,

    /// <summary>
    /// The Strategist and the Risk Critic use the same provider, so the critique is less independent
    /// (FR-ROLE-04). Allowed, so a user with one AI key can still run an agent (FR-ROLE-05).
    /// </summary>
    RiskCriticSharesProvider,

    /// <summary>The Sentiment Analyst's model can't search the web or X, which the role prefers (FR-ROLE-02).</summary>
    SentimentWithoutLiveSearch,

    /// <summary>The exchange key's Active agents have more capital allocated than the account has available (FR-AGT-13).</summary>
    CapitalExceedsBalance,
}

/// <param name="Role">Which role, for the model warnings.</param>
/// <param name="Asset">The quote asset, for <see cref="ActivationWarningKind.CapitalExceedsBalance"/>.</param>
/// <param name="Allocated">The total capital allocated by the key's Active agents in <paramref name="Asset"/>.</param>
/// <param name="Available">What the account has available in <paramref name="Asset"/>.</param>
public sealed record ActivationWarning(
    ActivationWarningKind Kind,
    AiRole? Role = null,
    string? Asset = null,
    decimal? Allocated = null,
    decimal? Available = null);

/// <summary>
/// What the user saw and agreed to when switching an agent to Live (UC-04). The page sends back the version, execution
/// mode and Risk Disclosure version it showed, so the switch is refused when any has changed since.
/// </summary>
/// <param name="Version">The agent's version the page showed.</param>
/// <param name="ExchangeCredentialId">The user's Binance Live key the agent will trade with.</param>
/// <param name="ExecutionMode">The execution mode the page showed and the user acknowledged.</param>
/// <param name="RiskDisclosureVersion">The version of the Risk Disclosure the page showed and the user acknowledged.</param>
/// <param name="RiskDisclosureAcknowledged">The user ticked the Risk Disclosure's acknowledgement.</param>
/// <param name="ExecutionModeAcknowledged">The user ticked the execution mode's acknowledgement.</param>
/// <param name="TwoFactorCode">A code from the user's authenticator, or a recovery code. Ignored when two-factor is off.</param>
public sealed record LiveSwitchRequest(
    int Version,
    Guid? ExchangeCredentialId,
    ExecutionMode ExecutionMode,
    string RiskDisclosureVersion,
    bool RiskDisclosureAcknowledged,
    bool ExecutionModeAcknowledged,
    string? TwoFactorCode)
{
    /// <summary>Never the code, which a log or an exception message could otherwise carry.</summary>
    public override string ToString() => $"{nameof(LiveSwitchRequest)} {{ {nameof(Version)} = {Version} }}";
}

public enum LiveSwitchOutcome
{
    /// <summary>The agent is Live and Active. <see cref="LiveSwitchResult.Warnings"/> may say what's worth knowing.</summary>
    Switched,

    /// <summary><see cref="LiveSwitchResult.Problems"/> says what has to be fixed first. Nothing changed, and the code wasn't used.</summary>
    Refused,

    /// <summary>The agent isn't a Paused or Stopped Demo agent. <see cref="LiveSwitchResult.Agent"/> has what it is.</summary>
    NotAllowed,

    /// <summary>The agent has open positions or orders, which have to be closed first (UC-04).</summary>
    OpenPositions,

    /// <summary>
    /// The agent, its execution mode or the Risk Disclosure changed since the page showed them, so nothing changed.
    /// Show it again.
    /// </summary>
    Changed,

    NotFound,

    /// <summary>The two-factor code was missing or wrong (FR-AUTH-05). Nothing changed. <see cref="LiveSwitchResult.TwoFactor"/> says why.</summary>
    TwoFactorRefused,
}

/// <param name="Agent">The agent as it is now, unless <see cref="LiveSwitchOutcome.NotFound"/>.</param>
/// <param name="Problems">Why it was refused, when <see cref="LiveSwitchOutcome.Refused"/>.</param>
/// <param name="Warnings">Things worth knowing that didn't stop it.</param>
/// <param name="TwoFactor">The check that refused, when <see cref="LiveSwitchOutcome.TwoFactorRefused"/>.</param>
public sealed record LiveSwitchResult(
    LiveSwitchOutcome Outcome,
    AgentRecord? Agent = null,
    IReadOnlyList<ActivationProblem>? Problems = null,
    IReadOnlyList<ActivationWarning>? Warnings = null,
    TwoFactorCheck? TwoFactor = null);
