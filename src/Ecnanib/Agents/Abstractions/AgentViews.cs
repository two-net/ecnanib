using System.Text.Json;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

// Everything the pages show. Views never carry a key value.

public sealed record AgentSummary(
    Guid Id,
    string Name,
    MarketType MarketType,
    string Symbol,
    CandleInterval Timeframe,
    string StrategyId,
    string StrategyName,
    AgentMode Mode,
    AgentState State,
    string? StateReason,
    DateTimeOffset? LastEvaluatedAt,
    EvaluationOutcome? LastOutcome,
    int OpenPositions,
    DateTimeOffset UpdatedAt,
    /// <summary>An enabled council role uses a model its provider no longer lists (FR-SYNC-03).</summary>
    bool HasDeprecatedModel);

/// <summary><see cref="CatalogStatus"/> is the model's status in the synced catalog, null when the catalog does not list it (a model entered by hand); Deprecated asks for a replacement (FR-SYNC-03).</summary>
public sealed record CouncilAssignmentView(CouncilRole Role, AiProvider Provider, string Model, bool Enabled, ModelStatus? CatalogStatus);

/// <summary><see cref="IsClosing"/> means a close-at-market order is waiting for its fill; <see cref="IsUnmanaged"/> marks a position left open on Stop (FR-AGT-06).</summary>
public sealed record OpenPositionView(
    Guid TradeId,
    PositionSide Side,
    decimal Size,
    decimal EntryPrice,
    decimal? CurrentStop,
    bool IsUnmanaged,
    bool IsClosing,
    bool IsPaper,
    DateTimeOffset OpenedAt);

public sealed record OpenOrderView(
    Guid OrderId,
    OrderPurpose Purpose,
    OrderType Type,
    OrderSide Side,
    decimal? Price,
    decimal? StopPrice,
    decimal Quantity,
    OrderStatus Status,
    bool IsPaper,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record EvaluationView(
    Guid Id,
    DateTimeOffset CandleOpenTime,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    EvaluationOutcome Outcome,
    string? OutcomeDetail,
    bool CouncilConsulted,
    DecisionAction? FinalAction);

/// <summary>Old and new values are the stored JSON as text (FR-AGT-07).</summary>
public sealed record AgentChangeView(DateTimeOffset Timestamp, string Field, string? OldValue, string? NewValue);

/// <summary>A "no new entry" window in UTC (FR-STR-05); the fields of the other kind are null.</summary>
public sealed record EntryWindowView(
    long Id,
    EntryWindowKind Kind,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    DayOfWeek? StartDay,
    TimeOnly? StartTime,
    DayOfWeek? EndDay,
    TimeOnly? EndTime);

public sealed record AgentDetail(
    Guid Id,
    string Name,
    MarketType MarketType,
    string Symbol,
    CandleInterval Timeframe,
    string StrategyId,
    int StrategyVersion,
    /// <summary>The catalog's newest version of the strategy; greater than <see cref="StrategyVersion"/> when an upgrade is available (FR-STR-06), null when the strategy left the catalog.</summary>
    int? LatestStrategyVersion,
    /// <summary>The stored overrides only; blank means the default of the pinned version.</summary>
    IReadOnlyDictionary<string, JsonElement> Parameters,
    AgentMode Mode,
    Guid? ExchangeKeyId,
    int? Leverage,
    MarginType? MarginType,
    RiskLimits RiskLimits,
    bool Discretionary,
    AgentState State,
    PauseCause? PauseCause,
    DateTimeOffset? AutoResumeAt,
    string? StateReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CouncilAssignmentView> Council,
    IReadOnlyList<EntryWindowView> EntryWindows,
    IReadOnlyList<OpenPositionView> OpenPositions,
    IReadOnlyList<OpenOrderView> OpenOrders,
    IReadOnlyList<EvaluationView> RecentEvaluations,
    IReadOnlyList<AgentChangeView> RecentChanges);
