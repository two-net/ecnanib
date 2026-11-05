using Ecnanib.Kernel;

namespace Ecnanib.Modules.RiskEngine.Contracts;

/// <summary>
/// Everything an evaluation looks at, as plain values, because the engine depends on Kernel alone (rule 4 in
/// docs/architecture.md). The decision cycle builds it from Market Data, the exchange and the trade journal
/// (#31); docs/architecture.md maps each field to its source. Amounts are in the quote asset and quantities
/// in the base asset. Nothing is read from a clock: <see cref="Now"/> is the caller's.
/// </summary>
public sealed record RiskInputs
{
    public required Market Market { get; init; }

    /// <summary>When the evaluation happens. Loss-limit pauses run from it.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>The agent's settings, as stored. The engine applies the caps to them.</summary>
    public required RiskSettings Settings { get; init; }

    /// <summary>The agent's capital allocation (FR-AGT-02).</summary>
    public required decimal CapitalAllocation { get; init; }

    public required ExchangeFilters Filters { get; init; }

    public required MarketPrices Prices { get; init; }

    /// <summary>ATR(14) of the agent's timeframe. Null when there aren't enough candles, which fails every entry.</summary>
    public decimal? Atr { get; init; }

    /// <summary>Futures only. Null when Binance gave no funding rate.</summary>
    public FundingInfo? Funding { get; init; }

    /// <summary>Spot: the free balance of the quote asset. Futures: the available margin balance.</summary>
    public required decimal AvailableQuote { get; init; }

    /// <summary>Spot only: everything the account holds of the base asset, free or locked. Null when unknown.</summary>
    public decimal? BaseHeld { get; init; }

    /// <summary>
    /// The agent's own open position, or null. On Spot, its quantity is what the agent's own fills acquired
    /// (FR-AGT-09).
    /// </summary>
    public OpenPosition? Position { get; init; }

    /// <summary>Whether the agent has an entry order that hasn't filled.</summary>
    public bool HasPendingEntry { get; init; }

    public TradingRecord Record { get; init; } = TradingRecord.None;

    /// <summary>
    /// Futures only: the symbol's leverage brackets, for the liquidation price. Without them every Futures entry
    /// fails, because the liquidation buffer can't be checked.
    /// </summary>
    public IReadOnlyList<LeverageBracket> Brackets { get; init; } = [];
}

/// <summary>
/// The symbol's exchange filters (FR-RISK-04). A filter Binance didn't send is null, and a 0 in one means
/// Binance turned that part off.
/// </summary>
/// <param name="Trading">Whether Binance lists the symbol as TRADING.</param>
/// <param name="Price">PRICE_FILTER.</param>
/// <param name="Lot">LOT_SIZE.</param>
/// <param name="MarketLot">MARKET_LOT_SIZE. A step of 0 means <paramref name="Lot"/>'s step applies.</param>
/// <param name="Notional">Spot's NOTIONAL or Futures' MIN_NOTIONAL.</param>
/// <param name="PercentPrice">PERCENT_PRICE or PERCENT_PRICE_BY_SIDE.</param>
public sealed record ExchangeFilters(
    bool Trading,
    TickRule? Price,
    LotRule? Lot,
    LotRule? MarketLot,
    NotionalRule? Notional,
    PercentPriceRule? PercentPrice);

/// <summary>Valid prices are Minimum + n × TickSize, up to Maximum.</summary>
public sealed record TickRule(decimal Minimum, decimal Maximum, decimal TickSize);

/// <summary>Valid quantities are Minimum + n × StepSize, up to Maximum.</summary>
public sealed record LotRule(decimal Minimum, decimal Maximum, decimal StepSize);

/// <param name="Maximum">Null when Binance sets none.</param>
public sealed record NotionalRule(decimal Minimum, decimal? Maximum);

/// <summary>
/// How far a limit price may be from the reference price, as multipliers of it: the mark price on Futures, and
/// the average price on Spot. A 0 is no bound. Futures bounds a buy from above and a sell from below only.
/// </summary>
public sealed record PercentPriceRule(decimal BuyUp, decimal BuyDown, decimal SellUp, decimal SellDown);

/// <param name="Mark">Futures only.</param>
public sealed record MarketPrices(decimal Bid, decimal Ask, decimal Last, decimal? Mark = null);

/// <param name="Rate">The last funding rate as a fraction, such as 0.0001. Positive means longs pay.</param>
/// <param name="IntervalHours">Hours between fundings, usually 8.</param>
public sealed record FundingInfo(decimal Rate, int IntervalHours = 8);

/// <param name="Quantity">Above zero, whatever the side.</param>
/// <param name="AverageEntry">The average entry price.</param>
/// <param name="BreakEven">Binance's break-even price, including fees, when known.</param>
/// <param name="Stop">The protective stop's trigger price.</param>
/// <param name="Target">The take-profit's price, if there is one.</param>
public sealed record OpenPosition(
    PositionSide Side,
    decimal Quantity,
    decimal AverageEntry,
    decimal? BreakEven = null,
    decimal? Stop = null,
    decimal? Target = null);

/// <summary>Stored by name, so never renamed.</summary>
public enum PositionSide
{
    Long,
    Short,
}

/// <summary>
/// The agent's recent results, from its trade journal. P&amp;L is realized, net of fees and funding, so a loss
/// is negative. Use <see cref="RiskPeriods"/> for the day and week, so they match the engine's.
/// </summary>
/// <param name="RealizedToday">Since <see cref="RiskPeriods.DayStart"/>.</param>
/// <param name="RealizedThisWeek">Since <see cref="RiskPeriods.WeekStart"/>.</param>
/// <param name="ConsecutiveLosses">
/// Losing trades in a row, newest first, closed since the agent last became Active. Any trade that isn't a
/// loss ends the run.
/// </param>
/// <param name="EntriesToday">Entry orders placed since <see cref="RiskPeriods.DayStart"/>, adds included.</param>
/// <param name="CyclesSinceLastLoss">
/// Decision cycles finished since the last losing trade closed, not counting this one. Null when there's been
/// no loss.
/// </param>
public sealed record TradingRecord(
    decimal RealizedToday,
    decimal RealizedThisWeek,
    int ConsecutiveLosses,
    int EntriesToday,
    int? CyclesSinceLastLoss)
{
    /// <summary>An agent with no trades.</summary>
    public static TradingRecord None { get; } = new(0m, 0m, 0, 0, null);
}

/// <summary>One of Binance's leverage brackets for the symbol (<c>/fapi/v1/leverageBracket</c>).</summary>
/// <param name="NotionalFloor">The bracket applies from this position notional.</param>
/// <param name="NotionalCap">Up to this one.</param>
/// <param name="MaintenanceMarginRate">As a fraction, such as 0.004.</param>
/// <param name="MaintenanceAmount">Binance's <c>cum</c>.</param>
/// <param name="InitialLeverage">The highest leverage the bracket allows.</param>
public sealed record LeverageBracket(
    decimal NotionalFloor,
    decimal NotionalCap,
    decimal MaintenanceMarginRate,
    decimal MaintenanceAmount,
    int InitialLeverage);
