namespace Ecnanib.Modules.RiskEngine.Contracts;

/// <summary>
/// What someone wants done on the exchange, in the engine's own terms (rule 4 in docs/architecture.md). The
/// council's trade proposal (Appendix B) never reaches the engine: the decision cycle copies the fields it
/// needs into this, so nothing a model writes can skip a rule (FR-RISK-01, NFR-SEC-08). Every price is in
/// the quote asset, and nothing here is trusted: the engine rounds, checks and sizes it.
/// </summary>
public sealed record OrderIntent
{
    public required IntentAction Action { get; init; }

    /// <summary>Who asked. The kill switch and the emergency close are <see cref="IntentOrigin.System"/>.</summary>
    public IntentOrigin Origin { get; init; } = IntentOrigin.Council;

    /// <summary>Entries only. Required for one.</summary>
    public EntryType? EntryType { get; init; }

    /// <summary>A limit entry's price. Ignored for a market entry.</summary>
    public decimal? LimitPrice { get; init; }

    /// <summary>An entry's protective stop, which is always required, or an adjustment's new stop.</summary>
    public decimal? StopPrice { get; init; }

    /// <summary>
    /// An entry's take-profit, which is optional, or an adjustment's new target, where null removes it. Appendix B
    /// allows several, and the caller passes the nearest, which gives the lowest reward-to-risk.
    /// </summary>
    public decimal? TargetPrice { get; init; }

    /// <summary>
    /// Entries only. The risk asked for, as a percentage of the capital allocation. Null asks for the agent's
    /// maximum, and anything above the maximum is brought down to it.
    /// </summary>
    public decimal? RiskPercent { get; init; }

    /// <summary>Sell and Close only. The share of the position to close, above 0 and at most 1. Null closes all of it.</summary>
    public decimal? CloseFraction { get; init; }
}

/// <summary>FR-CYC-03's actions, less HOLD, which asks nothing of the exchange. Stored by name, so never renamed.</summary>
public enum IntentAction
{
    /// <summary>Spot: open or add to a long.</summary>
    Buy,

    /// <summary>Spot: close or reduce the long.</summary>
    Sell,

    /// <summary>Futures: open or add to a long.</summary>
    OpenLong,

    /// <summary>Futures: open or add to a short.</summary>
    OpenShort,

    /// <summary>Futures: close or reduce the position, reduce-only.</summary>
    Close,

    /// <summary>Move the protective stop. Only toward less risk (FR-RISK-06).</summary>
    AdjustStop,

    /// <summary>Move or remove the take-profit.</summary>
    AdjustTarget,

    /// <summary>Cancel the unfilled entry order. Never a protective stop or target.</summary>
    CancelPending,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum IntentOrigin
{
    /// <summary>The AI council's proposal, after the Risk Critic.</summary>
    Council,

    /// <summary>Ecnanib itself: the kill switch (FR-AGT-08) or the emergency close (FR-EXE-02).</summary>
    System,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum EntryType
{
    Market,
    Limit,
}
