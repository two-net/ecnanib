using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.MarketData.Abstractions;

/// <summary>
/// The closed candles a chart starts from (FR-UI-03 c): the series the feed already holds for a tracked agent,
/// otherwise the <c>candles</c> table, otherwise the exchange's public data (a draft agent's symbol was never
/// followed). The chart then moves its last bar from <c>IMarketStreams.SubscribeCandleUpdates</c>.
/// </summary>
public interface ICandleReader
{
    /// <summary>At most <paramref name="count"/> closed candles, ascending by open time and ending with the newest closed one.</summary>
    Task<IReadOnlyList<Candle>> ReadAsync(MarketKey key, CandleInterval interval, int count, CancellationToken cancellationToken);
}
