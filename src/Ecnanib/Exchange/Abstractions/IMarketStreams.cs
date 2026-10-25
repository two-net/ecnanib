namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Live public market streams with automatic reconnection and REST polling fallback (EX-03, FR-DATA-03).
/// Subscriptions for the same key are shared; handlers run sequentially per subscription and may be slow without
/// stalling the exchange connection. Disposing the returned handle removes the handler; the last handler closes the stream.
/// </summary>
public interface IMarketStreams
{
    /// <summary>Delivers each closed candle exactly once, whether it arrived by stream or by polling.</summary>
    IDisposable SubscribeCandles(MarketKey key, CandleInterval interval, Func<Candle, CancellationToken, ValueTask> onClosed);

    /// <summary>
    /// Delivers every candle update of the same shared stream, the forming candle (<c>IsClosed</c> false) as often as
    /// the exchange sends it and the closed ones, without deduplication: what a chart needs to move its last bar
    /// (FR-UI-03 c). Polled candles are delivered too while the socket is down.
    /// </summary>
    IDisposable SubscribeCandleUpdates(MarketKey key, CandleInterval interval, Func<Candle, CancellationToken, ValueTask> onUpdate);

    IDisposable SubscribePrice(MarketKey key, Func<PriceTick, CancellationToken, ValueTask> onPrice);

    /// <summary>USD-M futures only. Throws <see cref="NotSupportedException"/> for a spot key.</summary>
    IDisposable SubscribeMarkPrice(MarketKey key, Func<MarkPrice, CancellationToken, ValueTask> onMark);

    IReadOnlyList<StreamStatus> GetStatuses();
}

/// <summary>Exchange-side health for the system status page (NFR-OBS-01).</summary>
public interface IExchangeHealth
{
    IReadOnlyList<StreamStatus> MarketStreams { get; }

    IReadOnlyList<StreamStatus> UserDataStreams { get; }

    RateLimitSnapshot RateLimits { get; }
}
