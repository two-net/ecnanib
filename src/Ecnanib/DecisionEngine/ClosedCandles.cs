using Ecnanib.ExchangeGateway;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Asks the exchange for the candles that a cycle starts with, until they reach the close that
/// the cycle is for (FR-CYC-1). See docs/cycles.md and "Candles" in docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// A cycle starts when a candle closes, and which candle is closed is the exchange's word, and
/// not the clock's. Just after a close the exchange may not have begun the next candle. The
/// candle that has closed by the clock of the service is then still the one in progress to the
/// exchange, which the gateway never gives: the candles come one short. So they are asked for
/// again, a second later, until the newest of them is the one that closed at the close.
/// </para>
/// <para>
/// It is asked once for a symbol and a timeframe on a market in an environment, for all the
/// agents that share them, and not once for each cycle: candles that came short are held for
/// nobody, so every ask for them is a read.
/// </para>
/// </remarks>
internal sealed class ClosedCandles(IExchangeMarketData market, TimeProvider clock)
{
    /// <summary>
    /// How long after a close the exchange is asked for its candle. The cycles of a close start
    /// within half a minute of it (NFR-PRF-3), and a candle that is not there by then is not
    /// waited for: its cycles are skipped, and the next close starts the next ones.
    /// </summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // A read that failed is shared for some seconds, and is no call then, so one pace does for
    // candles that came short and for an exchange that gives no answer.
    private static readonly TimeSpan Again = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The candles of a group through a close: the newest of them closed at
    /// <paramref name="closedAt"/>.
    /// </summary>
    /// <param name="group">The symbol and the timeframe, on a market in an environment.</param>
    /// <param name="closedAt">The close that the cycles are for.</param>
    /// <param name="cancellationToken">Ends the asking.</param>
    /// <returns>
    /// The candles, or none when the exchange did not give the candle of the close in time: with
    /// how the last read ended, which says whether the exchange answered at all.
    /// </returns>
    public async Task<(ExchangeCandles? Through, ExchangeCallOutcome Last)> ThroughAsync(
        CandleGroup group, DateTimeOffset closedAt, CancellationToken cancellationToken)
    {
        var interval = CandleIntervals.Of(group.Timeframe);

        while (true)
        {
            var candles = await market.GetCandlesAsync(group.Market, group.Mode, group.Symbol, interval, cancellationToken);

            if (candles.Outcome == ExchangeCallOutcome.Succeeded && candles.Candles.Count > 0)
            {
                var newest = candles.Candles[^1].ClosedAt;

                if (newest == closedAt)
                {
                    return (candles, candles.Outcome);
                }

                if (newest > closedAt)
                {
                    // A later candle has closed, so the close is over: its cycles would decide
                    // on candles that they are not for.
                    return (null, candles.Outcome);
                }
            }

            // Asked once at least, however late the cycles are.
            if (clock.GetUtcNow() >= closedAt + Wait)
            {
                return (null, candles.Outcome);
            }

            await Task.Delay(Again, clock, cancellationToken);
        }
    }
}
