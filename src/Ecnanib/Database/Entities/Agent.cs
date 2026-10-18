using System.Text.Json;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// A user-owned trading agent: one symbol on one market at one timeframe with one strategy and one council
/// configuration (§1.3, FR-AGT-01). State follows Appendix E.
/// </summary>
public sealed class Agent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public required string Name { get; set; }

    public MarketType MarketType { get; set; }

    public required string Symbol { get; set; }

    /// <summary>Exactly one of 15m, 1H, 4H, 1D (FR-AGT-02); the weekly interval is never an agent timeframe.</summary>
    public CandleInterval Timeframe { get; set; }

    /// <summary>Catalog key of the strategy (Appendix A).</summary>
    public required string StrategyId { get; set; }

    /// <summary>Pinned at creation; upgradable while Paused or Stopped (FR-STR-06).</summary>
    public int StrategyVersion { get; set; }

    /// <summary>Strategy parameters within their bounds (FR-STR-04); shape owned by the strategy engine.</summary>
    public JsonElement Parameters { get; set; } = JsonValues.EmptyObject;

    public AgentMode Mode { get; set; } = AgentMode.Paper;

    /// <summary>Required for live mode only (FR-KEY-09); cleared when the key is deleted (FR-KEY-06).</summary>
    public Guid? ExchangeKeyId { get; set; }

    public ExchangeKey? ExchangeKey { get; set; }

    /// <summary>Futures only; never above the configured cap (FR-AGT-13).</summary>
    public int? Leverage { get; set; }

    /// <summary>Futures only; isolated by default (FR-AGT-13).</summary>
    public MarginType? MarginType { get; set; }

    public RiskLimits RiskLimits { get; set; } = new();

    /// <summary>Lets the council propose entries without a strategy setup; off by default (FR-AI-09).</summary>
    public bool Discretionary { get; set; }

    public AgentState State { get; set; } = AgentState.Draft;

    public PauseCause? PauseCause { get; set; }

    /// <summary>When a daily-loss pause lifts (next UTC day); survives restarts (FR-EXE-09, NFR-REL-01).</summary>
    public DateTimeOffset? AutoResumeAt { get; set; }

    /// <summary>Human-readable cause of the current Paused or Error state (FR-KEY-07, FR-SYNC-04, FR-EXE-09).</summary>
    public string? StateReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint Version { get; set; }

    public ICollection<CouncilAssignment> CouncilAssignments { get; set; } = [];

    public ICollection<EntryWindow> EntryWindows { get; set; } = [];
}

/// <summary>Hard limits enforced after the council regardless of its output (FR-EXE-04, FR-EXE-09, FR-AGT-14).</summary>
public sealed class RiskLimits
{
    /// <summary>Default 1%, never above the configured cap (Appendix A).</summary>
    public decimal RiskPerTradePercent { get; set; } = 1m;

    public int MaxOpenPositions { get; set; } = 1;

    /// <summary>Default 3%; reaching it auto-pauses the agent until the next UTC day (FR-EXE-09).</summary>
    public decimal DailyLossLimitPercent { get; set; } = 3m;

    /// <summary>Optional cap on position notional (FR-EXE-04).</summary>
    public decimal? MaxNotional { get; set; }
}
