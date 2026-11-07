using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.Experience.Contracts;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// The figures of the settled trades one model took part in (FR-EXP-02), exact because they're computed from the
/// trades each time. P&amp;L in one quote asset is never added to another's, and only what was closed at a known price
/// counts. See Trading experience in docs/architecture.md.
/// </summary>
internal static class TradeStats
{
    /// <param name="cost">The model's own cost in the role over the same period, in US dollars.</param>
    /// <param name="unpricedTokens">Its tokens with no price, which mean <paramref name="cost"/> isn't all it cost.</param>
    /// <param name="usdQuoteAssets">Quote assets whose P&amp;L counts as US dollars.</param>
    public static TradeMetrics Compute(
        IReadOnlyCollection<SettledTrade> trades, decimal cost, long unpricedTokens, IReadOnlySet<string> usdQuoteAssets)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(usdQuoteAssets);

        var priced = trades.Where(trade => trade.ExitQuantity > 0m).OrderBy(trade => trade.ClosedAt).ThenBy(trade => trade.Id).ToList();
        var rMultiples = priced.Where(trade => trade.RMultiple is not null).Select(trade => trade.RMultiple!.Value).ToList();
        var allPriced = unpricedTokens == 0;

        decimal? net = allPriced && trades.All(trade => usdQuoteAssets.Contains(trade.QuoteAsset))
            ? priced.Sum(trade => trade.RealizedPnl) - cost
            : null;

        return new TradeMetrics(
            trades.Count,
            priced.Count,
            priced.Count(trade => trade.RealizedPnl > 0m),
            priced.Count(trade => trade.RealizedPnl < 0m),
            rMultiples.Count == 0 ? null : rMultiples.Average(),
            trades.Count == 0 ? null : TimeSpan.FromTicks((long)trades.Average(trade => (trade.ClosedAt - trade.OpenedAt).Ticks)),
            [.. priced.GroupBy(trade => trade.QuoteAsset, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select(Results)],
            allPriced && trades.Count > 0 ? cost / trades.Count : null,
            net);
    }

    /// <summary>The trades in one quote asset, in the order they closed.</summary>
    private static QuoteResults Results(IGrouping<string, SettledTrade> trades)
    {
        var total = 0m;
        var peak = 0m;
        var drawdown = 0m;

        foreach (var trade in trades)
        {
            total += trade.RealizedPnl;
            peak = Math.Max(peak, total);
            drawdown = Math.Max(drawdown, peak - total);
        }

        return new QuoteResults(
            trades.Key,
            trades.Count(),
            total,
            trades.Where(trade => trade.RealizedPnl > 0m).Sum(trade => trade.RealizedPnl),
            -trades.Where(trade => trade.RealizedPnl < 0m).Sum(trade => trade.RealizedPnl),
            drawdown);
    }
}
