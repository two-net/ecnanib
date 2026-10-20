using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>What the create form asks (FR-AGT-01); strategy, council and risk limits start at their defaults and are edited on the agent page.</summary>
public sealed record AgentCreateInput(
    string? Name,
    MarketType MarketType,
    string? Symbol,
    CandleInterval Timeframe,
    AgentMode Mode,
    Guid? ExchangeKeyId,
    int? Leverage,
    MarginType? MarginType);

/// <summary>The settings section of the agent page (FR-AGT-07: market, symbol, timeframe and mode only in Draft or Stopped).</summary>
public sealed record AgentSettingsInput(
    string? Name,
    MarketType MarketType,
    string? Symbol,
    CandleInterval Timeframe,
    AgentMode Mode,
    Guid? ExchangeKeyId,
    int? Leverage,
    MarginType? MarginType,
    bool Discretionary);

/// <summary>
/// The strategy section: a catalog id of the agent's timeframe and the parameter texts as typed (blank means the
/// default). <see cref="ResetToDefaults"/> discards every stored parameter (FR-STR-04).
/// </summary>
public sealed record StrategyInput(string? StrategyId, IReadOnlyDictionary<string, string?> Parameters, bool ResetToDefaults = false);

public sealed record CouncilRoleInput(CouncilRole Role, AiProvider Provider, string? Model, bool Enabled);

/// <summary>All four roles (FR-AI-03); the Risk Manager must stay enabled (FR-AI-05).</summary>
public sealed record CouncilInput(IReadOnlyList<CouncilRoleInput> Roles);

public sealed record RiskLimitsInput(decimal RiskPerTradePercent, int MaxOpenPositions, decimal DailyLossLimitPercent, decimal? MaxNotional);

/// <summary>
/// One "no new entry" window in UTC (FR-STR-05): a one-off window needs <see cref="StartAt"/> and <see cref="EndAt"/>,
/// a recurring one the four day and time fields; the fields of the other kind are ignored.
/// </summary>
public sealed record EntryWindowInput(
    EntryWindowKind Kind,
    DateTimeOffset? StartAt = null,
    DateTimeOffset? EndAt = null,
    DayOfWeek? StartDay = null,
    TimeOnly? StartTime = null,
    DayOfWeek? EndDay = null,
    TimeOnly? EndTime = null);
