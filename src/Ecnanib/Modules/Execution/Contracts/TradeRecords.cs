using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>Stored by name, so a value is never renamed.</summary>
public enum TradeStatus
{
    Open,
    Closed,
}

/// <summary>How a trade's last part ended. Stored by name, so a value is never renamed.</summary>
public enum TradeExit
{
    StopFilled,
    TargetFilled,

    /// <summary>An exit the council proposed sold it.</summary>
    Exited,

    /// <summary>Its stop couldn't be placed, so it was closed at market (FR-EXE-02).</summary>
    Unprotected,

    /// <summary>It was closed outside Ecnanib, such as by the user on Binance, or liquidated (FR-EXE-06).</summary>
    ClosedOutside,

    /// <summary>Its stop was cancelled outside Ecnanib after the agent was stopped, so the user now manages it.</summary>
    LeftToUser,

    /// <summary>Too small for Binance to take a stop or a close, so it's the user's.</summary>
    Dust,

    /// <summary>Binance rejected every close, so the user must close it.</summary>
    Abandoned,

    /// <summary>The kill switch closed it at market (FR-AGT-08).</summary>
    KillSwitch,
}

/// <summary>
/// An agent's results to date in one environment and one quote asset (FR-AGT-11). The pair can change while the agent
/// has nothing open, so there can be more than one.
/// </summary>
/// <param name="RealizedPnl">
/// Every closed trade's P&amp;L, net of fees and funding as far as they're known, in the quote asset. A loss is
/// negative. A trade not yet settled counts its fees and funding read so far, which leaves out its exit's fee for a
/// minute or so.
/// </param>
/// <param name="ClosedTrades">How many trades closed.</param>
/// <param name="Provisional">Closed trades not yet settled from Binance, whose figures may still change.</param>
/// <param name="FeesUnknown">Closed trades whose fees or funding Binance never gave, so they count before them.</param>
/// <param name="OpenTrades">Trades still open, an add counting as one of its own.</param>
/// <param name="OpenFees">What the open trades' fills have cost in fees so far, in the quote asset.</param>
/// <param name="OpenFunding">Futures: the funding the open trades have paid or received so far, positive when received.</param>
public sealed record JournalTotals(
    string QuoteAsset,
    decimal RealizedPnl,
    int ClosedTrades,
    int Provisional,
    int FeesUnknown,
    int OpenTrades,
    decimal OpenFees,
    decimal OpenFunding);

/// <summary>Which of an agent's trades to list: those in one environment that were open at some point in a range.</summary>
/// <param name="Symbol">Only trades in this pair, such as for the agent's chart. Null for every pair.</param>
/// <param name="From">Trades still open, or closed at or after this. Null for no limit.</param>
/// <param name="To">Trades opened at or before this. Null for no limit.</param>
/// <param name="Limit">At most this many, newest first, up to <see cref="MaxLimit"/>.</param>
public sealed record TradeQuery(
    TradingEnvironment Environment,
    string? Symbol = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 50)
{
    public const int MaxLimit = 200;
}

/// <summary>One trade in the journal, SRS 6.1's Trade. Prices are in the quote asset, quantities in the base asset.</summary>
/// <param name="Environment">The trade's own, which may not be the agent's now (FR-EXP-08).</param>
/// <param name="ExitPrice">The average price of what was closed with a known price, or null.</param>
/// <param name="Fees">Every fill's fee in the quote asset once settled. Null before, and when Binance never gave them.</param>
/// <param name="Funding">Futures: once settled. Positive when received.</param>
/// <param name="RealizedPnl">Once closed. Net of fees and funding once <paramref name="Settled"/>, and before them until then.</param>
/// <param name="Settled">Its figures came from Binance's fills and funding, rather than the order rows.</param>
public sealed record TradeRecord(
    Guid Id,
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    PositionSide Side,
    TradeStatus Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal Quantity,
    decimal EntryPrice,
    decimal InitialStop,
    decimal ExitQuantity,
    decimal? ExitPrice,
    decimal UnpricedQuantity,
    decimal? Fees,
    decimal? Funding,
    decimal? RealizedPnl,
    decimal? RMultiple,
    TradeExit? ExitReason,
    bool Settled);

/// <summary>Which of a user's settled trades to list: those in one environment that closed in a range.</summary>
/// <param name="ClosedFrom">Trades that closed at or after this.</param>
/// <param name="ClosedTo">And before this.</param>
/// <param name="Market">Only trades in this market. Null for both.</param>
/// <param name="Symbol">Only trades in this pair. Null for every pair.</param>
public sealed record SettledTradeQuery(
    TradingEnvironment Environment,
    DateTimeOffset ClosedFrom,
    DateTimeOffset ClosedTo,
    Market? Market = null,
    string? Symbol = null);

/// <summary>
/// A settled trade's final figures, for the trading experience metrics. Prices and P&amp;L are in the quote asset, and
/// quantities in the base asset.
/// </summary>
/// <param name="EntryCycleId">The cycle the trade's entry order cites, or null when its entry order is gone.</param>
/// <param name="Environment">The trade's own, which may not be the agent's now (FR-EXP-08).</param>
/// <param name="ExitQuantity">What was closed with a known price. Zero when nothing was priced, which P&amp;L leaves out.</param>
/// <param name="RealizedPnl">Net of fees and funding as far as Binance gave them. A loss is negative.</param>
/// <param name="RMultiple">The P&amp;L in multiples of what the trade risked, or null when that isn't known.</param>
public sealed record SettledTrade(
    Guid Id,
    Guid AgentId,
    Guid? EntryCycleId,
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    string QuoteAsset,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    decimal ExitQuantity,
    decimal RealizedPnl,
    decimal? RMultiple);
