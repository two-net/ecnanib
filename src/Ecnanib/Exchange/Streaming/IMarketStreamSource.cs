using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Streaming;

/// <summary>Exchange-specific side of the market streams: socket subscriptions and the REST polls used as fallback.</summary>
internal interface IMarketStreamSource
{
    /// <summary>Delivers every kline update; <see cref="Candle.IsClosed"/> marks the final update of a candle.</summary>
    Task<StreamSubscriptionHandle> SubscribeCandlesAsync(MarketKey key, CandleInterval interval, Action<Candle> onUpdate, CancellationToken ct);

    Task<StreamSubscriptionHandle> SubscribePriceAsync(MarketKey key, Action<PriceTick> onTick, CancellationToken ct);

    Task<StreamSubscriptionHandle> SubscribeMarkPriceAsync(MarketKey key, Action<MarkPrice> onMark, CancellationToken ct);

    /// <summary>Candles from <paramref name="fromOpenTime"/> onwards (or the most recent ones when null), ascending.</summary>
    Task<IReadOnlyList<Candle>> PollCandlesAsync(MarketKey key, CandleInterval interval, DateTimeOffset? fromOpenTime, int limit, CancellationToken ct);

    Task<PriceTick> PollPriceAsync(MarketKey key, CancellationToken ct);

    Task<MarkPrice> PollMarkPriceAsync(MarketKey key, CancellationToken ct);
}

/// <summary>Exchange-specific side of the user-data streams.</summary>
internal interface IUserDataSource
{
    /// <param name="onTerminated">The exchange ended the stream (listen key expired or stream terminated); subscribe again.</param>
    Task<StreamSubscriptionHandle> SubscribeAsync(ExchangeKeyHandle key, MarketType market, Action<UserDataEvent> onEvent, Action onTerminated, CancellationToken ct);

    /// <summary>REST snapshot of open orders, positions (futures) and balances, used while the stream is unavailable.</summary>
    Task<UserDataEvent.Snapshot> SnapshotAsync(ExchangeKeyHandle key, MarketType market, CancellationToken ct);
}
