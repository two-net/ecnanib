using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Stands in for the exchange module's streams: subscriptions are recorded and tests push closed candles and price ticks to them.</summary>
internal sealed class FakeMarketStreams : IMarketStreams
{
    private readonly object _lock = new();
    private readonly List<CandleSubscription> _candles = [];
    private readonly List<PriceSubscription> _prices = [];
    private int _candleSubscriptionsEver;
    private int _priceSubscriptionsEver;

    public int CandleSubscriptions
    {
        get
        {
            lock (_lock)
                return _candles.Count;
        }
    }

    public int PriceSubscriptions
    {
        get
        {
            lock (_lock)
                return _prices.Count;
        }
    }

    public int CandleSubscriptionsEver => Volatile.Read(ref _candleSubscriptionsEver);

    public int PriceSubscriptionsEver => Volatile.Read(ref _priceSubscriptionsEver);

    public IDisposable SubscribeCandles(MarketKey key, CandleInterval interval, Func<Candle, CancellationToken, ValueTask> onClosed)
    {
        Interlocked.Increment(ref _candleSubscriptionsEver);
        var subscription = new CandleSubscription(key, interval, onClosed);
        lock (_lock)
            _candles.Add(subscription);
        return new Unsubscriber(() =>
        {
            lock (_lock)
                _candles.Remove(subscription);
        });
    }

    public IDisposable SubscribePrice(MarketKey key, Func<PriceTick, CancellationToken, ValueTask> onPrice)
    {
        Interlocked.Increment(ref _priceSubscriptionsEver);
        var subscription = new PriceSubscription(key, onPrice);
        lock (_lock)
            _prices.Add(subscription);
        return new Unsubscriber(() =>
        {
            lock (_lock)
                _prices.Remove(subscription);
        });
    }

    public IDisposable SubscribeMarkPrice(MarketKey key, Func<MarkPrice, CancellationToken, ValueTask> onMark) => throw new NotSupportedException();

    public IReadOnlyList<StreamStatus> GetStatuses() => [];

    public bool HasCandleSubscription(MarketKey key, CandleInterval interval)
    {
        lock (_lock)
            return _candles.Any(s => s.Key == key && s.Interval == interval);
    }

    public bool HasPriceSubscription(MarketKey key)
    {
        lock (_lock)
            return _prices.Any(s => s.Key == key);
    }

    /// <summary>Delivers a closed candle to every handler on the stream, one after another, as the hub does.</summary>
    public async Task PushCandleAsync(MarketKey key, CandleInterval interval, Candle candle)
    {
        CandleSubscription[] targets;
        lock (_lock)
            targets = _candles.Where(s => s.Key == key && s.Interval == interval).ToArray();
        foreach (var target in targets)
            await target.Handler(candle, CancellationToken.None);
    }

    public async Task PushPriceAsync(MarketKey key, PriceTick tick)
    {
        PriceSubscription[] targets;
        lock (_lock)
            targets = _prices.Where(s => s.Key == key).ToArray();
        foreach (var target in targets)
            await target.Handler(tick, CancellationToken.None);
    }

    public static Candle ClosedCandle(string symbol, CandleInterval interval, DateTimeOffset openTime, decimal close = 100m) =>
        new(symbol, interval, openTime, openTime + interval.ToTimeSpan() - TimeSpan.FromMilliseconds(1), close, close + 1, close - 1, close, 10m, 1000m, 100, true);

    private sealed record CandleSubscription(MarketKey Key, CandleInterval Interval, Func<Candle, CancellationToken, ValueTask> Handler);

    private sealed record PriceSubscription(MarketKey Key, Func<PriceTick, CancellationToken, ValueTask> Handler);

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
