using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.MarketData.Services;

/// <summary>Serves a chart its history from the cheapest source that has enough of it: the feed's series, the audit table, the exchange.</summary>
internal sealed class CandleReader : ICandleReader
{
    private readonly CandleFeed _feed;
    private readonly CandleStore _store;
    private readonly IExchangeClientFactory _exchange;
    private readonly ILogger<CandleReader> _logger;

    public CandleReader(CandleFeed feed, CandleStore store, IExchangeClientFactory exchange, ILogger<CandleReader> logger)
    {
        _feed = feed;
        _store = store;
        _exchange = exchange;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Candle>> ReadAsync(MarketKey key, CandleInterval interval, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        // A tracked agent's series is contiguous and current: no query and no exchange call.
        if (_feed.Find(key, interval) is { IsReady: true } series)
        {
            var window = series.Window(DateTimeOffset.MaxValue, count);
            if (window.Count >= count || series.Exhausted)
                return window;
        }

        try
        {
            var stored = await _store.ReadRecentAsync(key, interval, count, cancellationToken).ConfigureAwait(false);
            if (stored.Count >= count)
                return stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The chart is not worth an error page; the exchange still has the history.
            _logger.LogDebug(ex, "Stored candles of {Key} {Interval} could not be read; reading the exchange instead", key, interval.ToLabel());
        }

        return await _exchange.GetPublicData(key.Environment, key.Market)
            .GetRecentCandlesAsync(key.Symbol, interval, count, closedOnly: true, cancellationToken).ConfigureAwait(false);
    }
}
