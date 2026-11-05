using Ecnanib.Kernel;

namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>An agent as it's stored.</summary>
/// <param name="QuoteAsset">The pair's quote asset, which the capital allocation is in (DATA-R-02).</param>
/// <param name="StatusReason">Why it's in its status: the user, or what paused it.</param>
/// <param name="Version">Changes with every change, so an edit can't overwrite one it hasn't seen.</param>
public sealed record AgentRecord(
    Guid Id,
    AgentSettings Settings,
    string QuoteAsset,
    AgentStatus Status,
    AgentStatusReason StatusReason,
    DateTimeOffset StatusChangedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public bool IsDemo => Settings.Environment == TradingEnvironment.Demo;
}

/// <summary>FR-AGT-04's states. Rows store the name, so a value is never renamed.</summary>
public enum AgentStatus
{
    /// <summary>Created, and never run since. Cloning makes one too.</summary>
    Draft,

    /// <summary>Runs a decision cycle at each candle close.</summary>
    Active,

    /// <summary>Runs no cycles. Its positions and protective orders stay, and reconciliation continues (FR-AGT-06).</summary>
    Paused,

    /// <summary>Runs no cycles, and no longer manages what it left on the exchange (FR-AGT-07).</summary>
    Stopped,

    /// <summary>Something went wrong that the user has to look at. Decision cycles set it (#31).</summary>
    Error,

    /// <summary>Removed, but kept (DATA-R-04). Only deleting it changes it.</summary>
    Archived,
}

/// <summary>Why an agent is in its status. Rows store the name, so a value is never renamed.</summary>
public enum AgentStatusReason
{
    /// <summary>The user put it there.</summary>
    User,

    /// <summary>Its exchange key was deleted (FR-KEY-10).</summary>
    CredentialDeleted,

    /// <summary>Its exchange key stopped being valid (FR-KEY-11).</summary>
    CredentialInvalid,

    /// <summary>Binance stopped allowing its exchange key to trade on its market (FR-KEY-05, FR-KEY-11).</summary>
    MarketNotEnabled,

    /// <summary>The user has no valid key left for a required role's provider (FR-KEY-10, FR-KEY-11).</summary>
    AiKeyUnavailable,

    /// <summary>A required role's model retired, and the role had no fallback that could take over (FR-CAT-04).</summary>
    ModelRetired,
}

/// <summary>The field names in an agent's audit summary (FR-AUD-02), which the Web UI gives text to.</summary>
/// <remarks>
/// None looks like a secret's name, which would mask its values. Records store them, so a value is never
/// renamed. Descriptions and custom instructions are never recorded, because administrators can search the
/// audit trail and must not see what users write to their models (FR-ADM-01).
/// </remarks>
public static class AgentAuditFields
{
    public const string Name = nameof(Name);

    /// <summary>A <see cref="Kernel.Market"/> name.</summary>
    public const string Market = nameof(Market);

    /// <summary>A <see cref="TradingEnvironment"/> name.</summary>
    public const string Environment = nameof(Environment);

    public const string Symbol = nameof(Symbol);

    /// <summary>A <see cref="Kernel.Timeframe"/> name.</summary>
    public const string Timeframe = nameof(Timeframe);

    /// <summary>The exchange key's label.</summary>
    public const string ExchangeKey = nameof(ExchangeKey);

    /// <summary>An <see cref="Contracts.ExecutionMode"/> name.</summary>
    public const string ExecutionMode = nameof(ExecutionMode);

    /// <summary><c>True</c> or <c>False</c>.</summary>
    public const string AutoExecuteRiskReducing = nameof(AutoExecuteRiskReducing);

    /// <summary>The amount and the quote asset, such as <c>1000 USDT</c>.</summary>
    public const string CapitalAllocation = nameof(CapitalAllocation);

    public const string DailyBudget = nameof(DailyBudget);

    public const string MonthlyBudget = nameof(MonthlyBudget);

    public const string DailyTokenCap = nameof(DailyTokenCap);

    public const string MonthlyTokenCap = nameof(MonthlyTokenCap);

    /// <summary><c>True</c> or <c>False</c>.</summary>
    public const string ExperienceMemory = nameof(ExperienceMemory);

    public const string ReflectionsInPrompt = nameof(ReflectionsInPrompt);

    /// <summary>An <see cref="AgentStatus"/> name.</summary>
    public const string Status = nameof(Status);

    /// <summary>An <see cref="AgentStatusReason"/> name.</summary>
    public const string Reason = nameof(Reason);

    /// <summary>The name of the agent a clone was made from.</summary>
    public const string ClonedFrom = nameof(ClonedFrom);

    /// <summary>The prefix of a role's model field, such as <c>Role.Strategist</c>, holding the model as <see cref="ModelRef"/> writes it.</summary>
    public const string RolePrefix = "Role.";

    /// <summary>The prefix of a role's fallback model field, such as <c>Fallback.Strategist</c>.</summary>
    public const string FallbackPrefix = "Fallback.";

    /// <summary>The prefix of a risk setting's field, such as <c>Risk.MaxLeverage</c>.</summary>
    public const string RiskPrefix = "Risk.";

    public static string RoleField(AiRole role) => RolePrefix + role;

    public static string FallbackField(AiRole role) => FallbackPrefix + role;

    public static string RiskField(RiskEngine.Contracts.RiskSetting setting) => RiskPrefix + setting;
}
