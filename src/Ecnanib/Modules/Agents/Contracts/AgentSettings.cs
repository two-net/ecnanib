using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// What a user sets on an agent (FR-AGT-02). A Draft may leave the exchange key, roles and capital out,
/// and activation then says what's missing (FR-AGT-05).
/// </summary>
public sealed record AgentSettings
{
    /// <summary>Unique among the user's agents that aren't archived, ignoring case. Only show it as text.</summary>
    public required string Name { get; init; }

    /// <summary>Only show it as text.</summary>
    public string? Description { get; init; }

    public required Market Market { get; init; }

    /// <summary>
    /// Demo, which every agent starts in. An agent becomes Live only through <see cref="IAgents.SwitchToLiveAsync"/>
    /// (UC-04), and an edit never changes it.
    /// </summary>
    public TradingEnvironment Environment { get; init; } = TradingEnvironment.Demo;

    /// <summary>One Binance symbol, such as BTCUSDT, which Binance must list as trading (FR-AGT-02).</summary>
    public required string Symbol { get; init; }

    public required Timeframe Timeframe { get; init; }

    /// <summary>The user's Binance key for <see cref="Environment"/> that the agent trades with.</summary>
    public Guid? ExchangeCredentialId { get; init; }

    public required ExecutionMode ExecutionMode { get; init; }

    /// <summary>In Confirm mode, whether risk-reducing actions run without approval (FR-APR-04). On by default.</summary>
    public bool AutoExecuteRiskReducing { get; init; } = true;

    /// <summary>At most one per role. The required ones are <see cref="AgentRoles.Required"/>.</summary>
    public IReadOnlyList<RoleAssignment> Roles { get; init; } = [];

    /// <summary>The most the agent may commit, in the pair's quote asset (FR-RISK-02).</summary>
    public decimal? CapitalAllocation { get; init; }

    public RiskSettings Risk { get; init; } = RiskSettings.Default;

    public AiBudget Budget { get; init; } = AiBudget.None;

    public ExperienceMemory Experience { get; init; } = ExperienceMemory.Default;

    /// <summary>Appended to prompts in a delimited section (FR-PRM-05). Only show it as text.</summary>
    public string? CustomInstructions { get; init; }

    /// <summary>The assignment for <paramref name="role"/>, or null.</summary>
    public RoleAssignment? RoleOf(AiRole role) => Roles.FirstOrDefault(assignment => assignment.Role == role);
}

/// <summary>A model, by the catalog's key: its provider and the ID the provider lists it by.</summary>
public sealed record ModelRef(string Provider, string ModelId)
{
    public override string ToString() => $"{Provider} {ModelId}";
}

/// <param name="Fallback">Used when the model fails (FR-ROLE-06). Optional.</param>
public sealed record RoleAssignment(AiRole Role, ModelRef Model, ModelRef? Fallback = null);

/// <summary>
/// The most an agent may spend on AI (FR-AGT-02), per UTC day and month, in US dollars, or in tokens where
/// a model's price isn't known: a call to a priced model counts its estimated cost, and a call to one without a
/// price counts its tokens. Every limit is optional. Once one is reached, the agent's cycles ask no AI and make
/// no new entry until its period ends (FR-CYC-07). See Budgets in docs/architecture.md.
/// </summary>
public sealed record AiBudget(decimal? DailyUsd, decimal? MonthlyUsd, long? DailyTokens, long? MonthlyTokens)
{
    public const string Currency = "USD";

    public static AiBudget None { get; } = new(null, null, null, null);
}

/// <summary>Whether the agent's reflections go into its prompts, and how many (FR-EXP-04).</summary>
public sealed record ExperienceMemory(bool Enabled, int ReflectionsInPrompt)
{
    public static ExperienceMemory Default { get; } = new(true, 5);
}

/// <summary>Rows store the name, so a value is never renamed.</summary>
public enum ExecutionMode
{
    Autonomous,

    /// <summary>Each trade waits for the user's approval (FR-APR-01).</summary>
    Confirm,
}

public static class AgentRoles
{
    /// <summary>
    /// The roles an agent can't be activated without (FR-ROLE-01, FR-AGT-05). The Sentiment Analyst and
    /// Reviewer are optional.
    /// </summary>
    public static IReadOnlyList<AiRole> Required { get; } = [AiRole.TechnicalAnalyst, AiRole.Strategist, AiRole.RiskCritic];

    public static bool IsRequired(AiRole role) => Required.Contains(role);
}

public static class AgentLimits
{
    /// <summary>After control characters are removed and the ends trimmed.</summary>
    public const int NameMaxLength = 60;

    public const int DescriptionMaxLength = 500;

    /// <summary>FR-PRM-05 asks for a limit, so that a user's text can't crowd out the rest of a prompt.</summary>
    public const int CustomInstructionsMaxLength = 2000;

    /// <summary>Binance's symbols are well under this.</summary>
    public const int SymbolMaxLength = 20;

    public const int MaxReflectionsInPrompt = 20;

    /// <summary>The largest capital allocation, budget or notional accepted, far above any real account.</summary>
    public const decimal MaxAmount = 1_000_000_000_000m;

    public const long MaxTokens = 1_000_000_000_000L;
}
