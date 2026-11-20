namespace Ecnanib.Experience;

/// <summary>
/// A track record: what a set of closed trades came to (FR-EXP-2). It is worked out from the
/// trades each time it is asked for, and is stored nowhere. See "The measures" in
/// docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every measure is in R,</b> the amount that was risked on a trade. The trades of a record
/// are of agents that are priced in different assets, whose amounts cannot be added up. A
/// multiple of what was risked can.
/// </para>
/// <para>
/// <b>A trade counts once it has a result in R.</b> One whose result is not recorded yet, and one
/// whose result says no R, is in no measure, so that every measure is of the same trades.
/// </para>
/// <para>
/// <b>A result that is an estimate counts.</b> A position that was liquidated, or that left its
/// agent by no order of the agent, has one, and leaving those out would leave out the worst
/// trades. The record says how many of its trades are estimates.
/// </para>
/// </remarks>
/// <param name="Trades">How many closed trades are counted: those with a result in R.</param>
/// <param name="Wins">How many of them made more than nothing.</param>
/// <param name="Losses">How many of them lost. A trade of exactly 0 R is neither a win nor a loss.</param>
/// <param name="WinRate">The wins among the counted trades, as a fraction from 0 to 1, or null for a record of no trades.</param>
/// <param name="AverageR">The mean result of a trade in R, or null for a record of no trades, and where it cannot be said.</param>
/// <param name="TotalR">The sum of the results in R, or null where it cannot be said.</param>
/// <param name="ProfitFactor">The R of the winning trades over the R of the losing ones, or null without a losing trade, and where it cannot be said.</param>
/// <param name="LargestDrawdownR">
/// The largest drawdown in R: the deepest fall of the running total of R from its highest point,
/// with the trades in the order they closed in and the total starting at 0. It is 0 or more, or
/// null where it cannot be said.
/// </param>
/// <param name="AverageHoldingTime">The mean time a trade was held, or null for a record of no trades.</param>
/// <param name="Estimates">How many of the counted trades have a result that is an estimate.</param>
/// <param name="SmallSample">Whether the record counts fewer trades than the minimum sample: a small sample is unreliable (FR-EXP-11).</param>
public sealed record TrackRecord(
    int Trades,
    int Wins,
    int Losses,
    decimal? WinRate,
    decimal? AverageR,
    decimal? TotalR,
    decimal? ProfitFactor,
    decimal? LargestDrawdownR,
    TimeSpan? AverageHoldingTime,
    int Estimates,
    bool SmallSample)
{
    /// <summary>
    /// The track record of some closed trades, in whatever order they are handed over. Demo and
    /// Live are scored separately (FR-EXP-9): whoever asks hands over the trades of one mode.
    /// </summary>
    /// <param name="trades">The trades. One without a result in R is in no measure.</param>
    /// <param name="minSamples">The sample size below which a record is a small sample: <c>Experience:MinSamples</c>.</param>
    public static TrackRecord Of(IEnumerable<ClosedTrade> trades, int minSamples)
    {
        ArgumentNullException.ThrowIfNull(trades);

        // In the order they closed in, which the drawdown goes by. Two trades of one moment are
        // told apart by their ids, so the same trades always give the same record.
        var counted = trades
            .Where(trade => trade.Result?.ResultInR is not null)
            .OrderBy(trade => trade.ClosedAt)
            .ThenBy(trade => trade.PositionId)
            .Select(trade => (R: trade.Result!.ResultInR!.Value, trade.Result.HoldingSeconds, trade.Result.Complete))
            .ToList();

        var wins = counted.Count(trade => trade.R > 0m);
        var losses = counted.Count(trade => trade.R < 0m);
        var sums = Sums.Of([.. counted.Select(trade => trade.R)]);

        return new TrackRecord(
            counted.Count,
            wins,
            losses,
            counted.Count == 0 ? null : (decimal)wins / counted.Count,
            counted.Count == 0 ? null : sums?.Total / counted.Count,
            sums?.Total,
            sums?.Factor,
            sums?.Drawdown,

            // A mean of whole seconds, which no number of trades brings out of range.
            counted.Count == 0 ? null : TimeSpan.FromSeconds(counted.Average(trade => (double)trade.HoldingSeconds)),
            counted.Count(trade => !trade.Complete),
            counted.Count < minSamples);
    }

    /// <summary>What the results of some trades add up to, in the order they closed in.</summary>
    /// <param name="Total">The sum of the results.</param>
    /// <param name="Factor">The sum of the results above 0 over the sum of those below 0, as an amount above 0, or null where no result is below 0.</param>
    /// <param name="Drawdown">The deepest fall of the running total from its highest point, starting at 0.</param>
    private sealed record Sums(decimal Total, decimal? Factor, decimal Drawdown)
    {
        /// <summary>
        /// The sums, or null where they are out of the range of a number: a trade on which next
        /// to nothing was risked has a result of very many R. Nothing is said then, and nothing
        /// fails.
        /// </summary>
        public static Sums? Of(IReadOnlyList<decimal> results)
        {
            try
            {
                var (total, won, lost, peak, drawdown) = (0m, 0m, 0m, 0m, 0m);

                foreach (var result in results)
                {
                    total += result;
                    won += Math.Max(result, 0m);
                    lost += Math.Max(-result, 0m);
                    peak = Math.Max(peak, total);
                    drawdown = Math.Max(drawdown, peak - total);
                }

                return new Sums(total, lost > 0m ? won / lost : null, drawdown);
            }
            catch (OverflowException)
            {
                return null;
            }
        }
    }
}
