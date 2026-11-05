using Ecnanib.Kernel;
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

    /// <summary>Switching to Live comes later (#39), so an agent can only be Demo.</summary>
    LiveNotAvailable,

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
/// <param name="RiskSettings">Which risk settings are outside their range, when <see cref="Fields"/> has <see cref="AgentField.Risk"/>.</param>
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

/// <summary>Something that stops an agent being activated (FR-AGT-05, FR-AGT-10).</summary>
public enum ActivationProblemKind
{
    /// <summary>Switching to Live comes later (#39).</summary>
    LiveNotAvailable,

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
    /// Another agent that's Active, Paused or in Error trades the same pair on the same market with the same
    /// exchange key (FR-AGT-10). <see cref="ActivationProblem.OtherAgent"/> names it.
    /// </summary>
    SlotTaken,
}

/// <param name="Role">Which role, for the role problems.</param>
/// <param name="OtherAgent">The other agent's name, for <see cref="ActivationProblemKind.SlotTaken"/>. Only show it as text.</param>
public sealed record ActivationProblem(ActivationProblemKind Kind, AiRole? Role = null, string? OtherAgent = null);

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
