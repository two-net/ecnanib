using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Execution.Abstractions;

/// <summary>The steps of a trade's life, in the order the timeline shows them (FR-UI-03 e).</summary>
public enum TimelineKind
{
    /// <summary>The evaluation that produced the entry decision started.</summary>
    Evaluated = 1,
    /// <summary>The council decided to open.</summary>
    Decided = 2,
    EntryPlaced = 3,
    EntryFilled = 4,
    /// <summary>The entry order ended without a fill (cancelled, expired or rejected).</summary>
    EntryEnded = 5,
    /// <summary>The initial stop and the targets the trade was protected with.</summary>
    Protected = 6,
    StopMoved = 7,
    /// <summary>A managing decision of the council (hold, modify, close); the change it ordered follows it.</summary>
    Managed = 8,
    TargetsChanged = 9,
    PartialExit = 10,
    Exit = 11,
    Closed = 12,
}

/// <summary>One step; the fields a kind does not need are null. <see cref="Reason"/> is the stop move's reason or the protection's targets as text.</summary>
public sealed record TimelineEvent(
    DateTimeOffset At,
    TimelineKind Kind,
    decimal? Price = null,
    decimal? Quantity = null,
    string? Reason = null,
    OrderStatus? Status = null,
    OrderPurpose? Purpose = null,
    DecisionAction? Action = null,
    TradeExitReason? ExitReason = null,
    decimal? RealizedPnl = null,
    Guid? EvaluationId = null,
    Guid? OrderId = null,
    IReadOnlyList<decimal>? Targets = null);

public sealed record TradeTimelineView(
    Guid TradeId,
    Guid AgentId,
    string Symbol,
    PositionSide Side,
    bool IsOpen,
    bool IsPaper,
    decimal EntryPrice,
    decimal Size,
    decimal? CurrentStop,
    decimal? RealizedPnl,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    TradeExitReason? ExitReason,
    IReadOnlyList<TimelineEvent> Events);

/// <summary>Builds a trade's lifecycle from its evaluation, decisions, orders and stop history (FR-UI-03 e), for its owner only (NFR-SEC-03).</summary>
public interface ITradeTimelineReader
{
    /// <summary>Null for another user's trade and a missing one.</summary>
    Task<TradeTimelineView?> GetAsync(Guid userId, Guid tradeId, CancellationToken cancellationToken);

    /// <summary>The agent's open trades first, then the most recently closed ones, at most <paramref name="count"/>; empty for another user's agent.</summary>
    Task<IReadOnlyList<TradeTimelineView>> ListRecentAsync(Guid userId, Guid agentId, int count, CancellationToken cancellationToken);
}
