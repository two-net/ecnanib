using Ecnanib.Shared;

namespace Ecnanib.RiskGuard;

/// <summary>What a plan that reaches the Risk Guard does. A plan that holds changes nothing, and reaches no check.</summary>
public enum GuardedAction
{
    /// <summary>It opens a long position.</summary>
    OpenLong,

    /// <summary>It opens a short position.</summary>
    OpenShort,

    /// <summary>It closes the open position, and opens one on the opposite side.</summary>
    Reverse,

    /// <summary>It closes the open position.</summary>
    Close,

    /// <summary>It moves the stop-loss or the take-profit of the open position.</summary>
    AdjustProtection,
}

/// <summary>The side of a position.</summary>
public enum TradeSide
{
    /// <summary>It gains when the price rises.</summary>
    Long,

    /// <summary>It gains when the price falls.</summary>
    Short,
}

/// <summary>The rules that an order in a symbol must keep, as the exchange says them.</summary>
/// <param name="Trading">Whether the exchange takes orders in the symbol now.</param>
/// <param name="PriceStep">What a price must be a multiple of. Zero or less is no rule.</param>
/// <param name="QuantityStep">What a quantity must be a multiple of. Zero or less is no rule.</param>
/// <param name="MinValue">The least value of an order: its price times its quantity. Zero or less is no rule.</param>
public sealed record SymbolRules(bool Trading, decimal PriceStep, decimal QuantityStep, decimal MinValue);

/// <summary>The risk settings of the agent that a check holds a plan to (SRS Appendix B).</summary>
/// <param name="MinRewardToRisk">The lowest reward-to-risk ratio at the entry.</param>
/// <param name="MaxEntryDistancePercent">How far an entry price may be from the current price, in percent of the current price.</param>
/// <param name="LiquidationBuffer">How many times further from the entry than the stop-loss the liquidation price must be, on Futures.</param>
/// <param name="DailyLossPercent">The loss in one UTC day that the agent may not reach, in percent of its equity as the day began.</param>
/// <param name="DrawdownPercent">The fall from its peak equity that the agent may not reach, in percent of the peak.</param>
public sealed record GuardLimits(
    decimal MinRewardToRisk,
    decimal MaxEntryDistancePercent,
    decimal LiquidationBuffer,
    decimal DailyLossPercent,
    decimal DrawdownPercent);

/// <summary>The size of the order that a plan opens, which the system computes and no model chooses (FR-RSK-5).</summary>
/// <param name="Quantity">How much of the asset that is traded the entry is for.</param>
/// <param name="Leverage">The leverage of the position on Futures, and 1 on Spot.</param>
public sealed record OrderSize(decimal Quantity, int Leverage);

/// <summary>The position that the agent holds as its plan is checked.</summary>
/// <param name="Side">Long or short.</param>
/// <param name="Quantity">How much of the asset it holds, which is what an order that closes or protects it is for.</param>
/// <param name="StopLoss">The price of the stop-loss that rests on the exchange, or null when none does.</param>
public sealed record GuardedPosition(TradeSide Side, decimal Quantity, decimal? StopLoss);

/// <summary>
/// Everything that one check of the Risk Guard decides on: the plan, and the facts that its
/// rules are asked of. The Risk Guard is rules only: whatever asks it gathers the facts, and it
/// reads nothing itself. See "The facts" in docs/risk-guard.md.
/// </summary>
/// <remarks>
/// It has no place for a switch: nothing in it turns a rule off, and which rules a plan is held
/// to follows from <see cref="Action"/> and <see cref="Market"/> alone (FR-RSK-3).
/// </remarks>
/// <param name="Action">What the plan does.</param>
/// <param name="Market">The market of the agent.</param>
/// <param name="EntryPrice">The price of a limit entry. Null for a market entry, which enters at the current price, and for a plan that opens nothing.</param>
/// <param name="StopLoss">The stop-loss of the position that the plan opens, or the one that it moves the stop-loss to. Null when the plan names none.</param>
/// <param name="TakeProfit">The take-profit of the position that the plan opens, or the one that it moves the take-profit to. Null when the plan names none.</param>
/// <param name="CurrentPrice">The price that the symbol last traded at, or null when the exchange did not say.</param>
/// <param name="SymbolsKnown">Whether the exchange said which symbols the market has in the agent's environment.</param>
/// <param name="Symbol">The rules of the agent's symbol. Null when the market does not have the symbol, and when the exchange did not say.</param>
/// <param name="Limits">The risk settings of the agent.</param>
/// <param name="Size">The size of the order that the plan opens, or null when it is not known.</param>
/// <param name="Position">The position that the agent holds now, or null when it holds none.</param>
/// <param name="Equity">The equity of the agent now, with unrealized profit and loss.</param>
/// <param name="EquityAtDayStart">Its equity as the UTC day began.</param>
/// <param name="PeakEquity">The highest that its equity has been.</param>
/// <param name="SinceClose">How long after the close of the candle of the cycle the plan is checked.</param>
/// <param name="StaleLimit">The stale limit of the agent's timeframe.</param>
/// <param name="ModePermitted">Whether the mode of the agent is permitted.</param>
/// <param name="MarketPermitted">Whether the market of the agent is permitted.</param>
/// <param name="GlobalHalt">Whether a global halt is on.</param>
public sealed record GuardCase(
    GuardedAction Action,
    Market Market,
    decimal? EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? CurrentPrice,
    bool SymbolsKnown,
    SymbolRules? Symbol,
    GuardLimits Limits,
    OrderSize? Size,
    GuardedPosition? Position,
    decimal Equity,
    decimal EquityAtDayStart,
    decimal PeakEquity,
    TimeSpan SinceClose,
    TimeSpan StaleLimit,
    bool ModePermitted,
    bool MarketPermitted,
    bool GlobalHalt)
{
    /// <summary>
    /// Whether the plan opens a position: open long, open short, and reverse do. Such a plan is
    /// held to every rule. A plan that only closes a position or adjusts protection is held to
    /// the rules of the exchange and to protection that only tightens, so that risk can always
    /// be reduced (FR-RSK-4).
    /// </summary>
    public bool OpensPosition => Action is GuardedAction.OpenLong or GuardedAction.OpenShort or GuardedAction.Reverse;
}
