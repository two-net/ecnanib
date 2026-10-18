using System.Text.Json;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// A position from entry to exit with its outcome (FR-EXP-02, FR-EXE-12). Retained for the life of the account
/// (NFR-DATA-03). <see cref="IsUnmanaged"/> marks positions left open on Stop (FR-AGT-06).
/// </summary>
public sealed class Trade
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }

    public Agent Agent { get; set; } = null!;

    public Guid EntryDecisionId { get; set; }

    public Decision EntryDecision { get; set; } = null!;

    /// <summary>Snapshots of the agent settings at entry, so history survives a re-pointed agent (FR-AGT-07, FR-EXP-04).</summary>
    public required string Symbol { get; set; }

    public MarketType MarketType { get; set; }

    public CandleInterval Timeframe { get; set; }

    public PositionSide Side { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal? ExitPrice { get; set; }

    public decimal Size { get; set; }

    /// <summary>Stops are only ever tightened (FR-AI-15); <see cref="StopHistory"/> keeps every value.</summary>
    public decimal? CurrentStop { get; set; }

    /// <summary>Array of { at, price, reason }.</summary>
    public JsonElement StopHistory { get; set; } = JsonValues.EmptyArray;

    /// <summary>Array of target prices, possibly with partial fractions.</summary>
    public JsonElement Targets { get; set; } = JsonValues.EmptyArray;

    public decimal Fees { get; set; }

    /// <summary>Futures funding paid or received (FR-EXE-12).</summary>
    public decimal Funding { get; set; }

    public decimal? RealizedPnl { get; set; }

    public decimal? RMultiple { get; set; }

    public decimal? MaxAdverseExcursion { get; set; }

    public decimal? MaxFavorableExcursion { get; set; }

    public TradeExitReason? ExitReason { get; set; }

    public bool IsPaper { get; set; }

    public bool IsUnmanaged { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint Version { get; set; }
}
