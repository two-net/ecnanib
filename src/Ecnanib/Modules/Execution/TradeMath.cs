using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// The trade journal's arithmetic, on plain values, so it's tested on its own. Prices are in the quote asset,
/// quantities in the base asset, and every amount of money in the quote asset (DATA-R-02). See The trade journal in
/// docs/architecture.md.
/// </summary>
internal static class TradeMath
{
    /// <summary>The candle lengths excursions are read at, finest first. Each divides a UTC day, so all align to the epoch.</summary>
    private static readonly (CandleInterval Interval, TimeSpan Length)[] ExcursionIntervals =
    [
        (CandleInterval.OneMinute, TimeSpan.FromMinutes(1)),
        (CandleInterval.FifteenMinutes, TimeSpan.FromMinutes(15)),
        (CandleInterval.OneHour, TimeSpan.FromHours(1)),
        (CandleInterval.FourHours, TimeSpan.FromHours(4)),
        (CandleInterval.OneDay, TimeSpan.FromDays(1)),
    ];

    /// <summary>
    /// A part that still stands for what it held: one that ended as <see cref="ProtectionEnd.Split"/> is replaced by its
    /// children, which keep its opening time and price and add up to its quantity. So the leaves of an entry's parts
    /// partition what it held.
    /// </summary>
    public static bool IsLeaf(ProtectionRow part) => !(part.Status == ProtectionStatus.Ended && part.EndReason == ProtectionEnd.Split);

    /// <summary>No longer the agent's to look after.</summary>
    public static bool IsTerminal(ProtectionRow part) =>
        part.Status is ProtectionStatus.Closed or ProtectionStatus.Ended or ProtectionStatus.Dust or ProtectionStatus.Abandoned;

    public static List<ProtectionRow> Leaves(IEnumerable<ProtectionRow> parts) => [.. parts.Where(IsLeaf)];

    /// <summary>When a terminal part ended. Dust and Abandoned parts from before the journal have only their last update.</summary>
    public static DateTimeOffset EndOf(ProtectionRow part) => part.EndedAt ?? part.UpdatedAt;

    /// <summary>What the leaves held at <paramref name="at"/>: those open by then that hadn't ended.</summary>
    public static decimal HeldAt(IEnumerable<ProtectionRow> leaves, DateTimeOffset at) =>
        leaves.Where(leaf => leaf.OpenedAt <= at && (!IsTerminal(leaf) || EndOf(leaf) > at)).Sum(leaf => leaf.Quantity);

    public static decimal Sign(PositionSide side) => side == PositionSide.Long ? 1m : -1m;

    /// <summary>How one leaf ended, as a trade's exit reason.</summary>
    public static TradeExit ExitOf(ProtectionRow leaf) => leaf.Status switch
    {
        ProtectionStatus.Dust => TradeExit.Dust,
        ProtectionStatus.Abandoned => TradeExit.Abandoned,
        _ => leaf.EndReason switch
        {
            ProtectionEnd.StopFilled => TradeExit.StopFilled,
            ProtectionEnd.TargetFilled => TradeExit.TargetFilled,
            ProtectionEnd.Exited => TradeExit.Exited,
            ProtectionEnd.Unprotected => TradeExit.Unprotected,
            ProtectionEnd.ClosedOutside => TradeExit.ClosedOutside,
            ProtectionEnd.LeftToUser => TradeExit.LeftToUser,
            ProtectionEnd.KillSwitch => TradeExit.KillSwitch,
            _ => throw new ArgumentException($"Part {leaf.Id} hasn't ended as a leaf does.", nameof(leaf)),
        },
    };

    /// <summary>
    /// How the trade ended: how its last leaf ended, ties going to the later sequence. Dust is passed over unless every
    /// leaf is dust, because it's what an exit or a close left, not what closed the trade.
    /// </summary>
    public static TradeExit ExitReason(IReadOnlyCollection<ProtectionRow> leaves)
    {
        var candidates = leaves.Any(leaf => leaf.Status != ProtectionStatus.Dust)
            ? leaves.Where(leaf => leaf.Status != ProtectionStatus.Dust)
            : leaves;

        return ExitOf(candidates.OrderBy(EndOf).ThenBy(leaf => leaf.Sequence).Last());
    }

    /// <summary>The average price of the fills, by quantity. Null without any.</summary>
    public static decimal? Average(IEnumerable<(decimal Price, decimal Quantity)> fills)
    {
        var (notional, quantity) = fills.Aggregate((0m, 0m), (sum, fill) => (sum.Item1 + (fill.Price * fill.Quantity), sum.Item2 + fill.Quantity));

        return quantity > 0m ? notional / quantity : null;
    }

    /// <summary>What the exits made before fees and funding: positive when a long sold above its entry.</summary>
    public static decimal Gross(PositionSide side, decimal entryPrice, decimal exitNotional, decimal exitQuantity) =>
        Sign(side) * (exitNotional - (entryPrice * exitQuantity));

    /// <summary>The realized P&amp;L in multiples of what the exits risked at the initial stop. Null when nothing was priced.</summary>
    public static decimal? RMultiple(decimal realized, decimal entryPrice, decimal initialStop, decimal exitQuantity)
    {
        var risk = Math.Abs(entryPrice - initialStop) * exitQuantity;

        return risk > 0m ? realized / risk : null;
    }

    /// <summary>
    /// A commission in the quote asset, when there's a way to tell without another price: as it is in the quote asset,
    /// and at the fill's price in the base asset. Null for any other asset, such as BNB.
    /// </summary>
    public static decimal? FeeInQuote(decimal fee, string asset, string baseAsset, string quoteAsset, decimal fillPrice) =>
        asset == quoteAsset ? fee
        : asset == baseAsset ? fee * fillPrice
        : null;

    /// <summary>
    /// The trade's share of a funding payment, by what it held at the time among all the agent's trades in the pair.
    /// Zero when the agent held nothing then, by its records.
    /// </summary>
    public static decimal FundingShare(decimal payment, decimal tradeHeld, decimal agentHeld) =>
        agentHeld > 0m && tradeHeld > 0m ? Math.Round(payment * tradeHeld / agentHeld, 8, MidpointRounding.ToZero) : 0m;

    /// <summary>
    /// The most the price went against the trade and its way while it was open, by its lowest and highest prices,
    /// times its quantity. Never below zero.
    /// </summary>
    public static (decimal Adverse, decimal Favorable) Excursions(PositionSide side, decimal entryPrice, decimal quantity, decimal low, decimal high) =>
        side == PositionSide.Long
            ? (Math.Max(0m, entryPrice - low) * quantity, Math.Max(0m, high - entryPrice) * quantity)
            : (Math.Max(0m, high - entryPrice) * quantity, Math.Max(0m, entryPrice - low) * quantity);

    /// <summary>
    /// The finest candles that cover the trade's life in one request, and the open time of the first. Null when even
    /// days would take more than one, which is a trade of nearly three years.
    /// </summary>
    public static (CandleInterval Interval, DateTimeOffset From, int Limit)? ExcursionCandles(DateTimeOffset openedAt, DateTimeOffset closedAt)
    {
        foreach (var (interval, length) in ExcursionIntervals)
        {
            var from = Floor(openedAt, length);
            var count = (int)Math.Min(int.MaxValue, ((closedAt - from).Ticks / length.Ticks) + 1);

            if (count <= IExchangeMarketData.MaximumCandles)
            {
                return (interval, from, count);
            }
        }

        return null;
    }

    /// <summary>The start of the candle of <paramref name="length"/> that holds <paramref name="at"/>, in UTC.</summary>
    public static DateTimeOffset Floor(DateTimeOffset at, TimeSpan length)
    {
        var ticks = at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;

        return new DateTimeOffset(at.UtcTicks - (((ticks % length.Ticks) + length.Ticks) % length.Ticks), TimeSpan.Zero);
    }

    /// <summary>When settling is tried again after <paramref name="attempts"/> failures: 1, 2, 4… minutes, up to an hour.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(60, 1 << Math.Clamp(attempts - 1, 0, 6)));
}
