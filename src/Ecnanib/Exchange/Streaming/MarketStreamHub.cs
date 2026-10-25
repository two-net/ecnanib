using System.Collections.Concurrent;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange.Streaming;

/// <summary>
/// Owns every market stream subscription in the process (NFR-ARCH-01). Subscriptions are shared per key and interval
/// and reference-counted: the last handler to leave closes the socket subscription.
/// </summary>
internal sealed class MarketStreamHub : BackgroundService, IMarketStreams
{
    private readonly IMarketStreamSource _source;
    private readonly IOptions<ExchangeOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MarketStreamHub> _logger;
    private readonly ConcurrentDictionary<string, IStreamEntry> _streams = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public MarketStreamHub(IMarketStreamSource source, IOptions<ExchangeOptions> options, TimeProvider time, ILoggerFactory loggerFactory)
    {
        _source = source;
        _options = options;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MarketStreamHub>();
    }

    public IDisposable SubscribeCandles(MarketKey key, CandleInterval interval, Func<Candle, CancellationToken, ValueTask> onClosed)
    {
        ArgumentNullException.ThrowIfNull(onClosed);
        return Subscribe(CandleStreamSubscription.NameFor(key, interval), () => CreateCandleStream(key, interval), s => s.AddHandler(onClosed));
    }

    public IDisposable SubscribeCandleUpdates(MarketKey key, CandleInterval interval, Func<Candle, CancellationToken, ValueTask> onUpdate)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        // The same shared stream as the closed-candle subscribers; an update handler keeps it open like any other.
        return Subscribe(CandleStreamSubscription.NameFor(key, interval), () => CreateCandleStream(key, interval), s => ((CandleStreamSubscription)s).AddUpdateHandler(onUpdate));
    }

    public IDisposable SubscribePrice(MarketKey key, Func<PriceTick, CancellationToken, ValueTask> onPrice)
    {
        ArgumentNullException.ThrowIfNull(onPrice);
        var name = $"price:{key.Environment}:{key.Market}:{key.Symbol}".ToLowerInvariant();
        return Subscribe(name, () => new TickStreamSubscription<PriceTick>(
            name,
            (handler, ct) => _source.SubscribePriceAsync(key, handler, ct),
            ct => _source.PollPriceAsync(key, ct),
            _options.Value.Streams.PricePollInterval,
            _options.Value.Streams, _time, _loggerFactory.CreateLogger<TickStreamSubscription<PriceTick>>()), s => s.AddHandler(onPrice));
    }

    public IDisposable SubscribeMarkPrice(MarketKey key, Func<MarkPrice, CancellationToken, ValueTask> onMark)
    {
        ArgumentNullException.ThrowIfNull(onMark);
        if (key.Market != MarketType.UsdFutures)
            throw new NotSupportedException("Mark price streams exist only on USD-M futures.");

        var name = $"mark:{key.Environment}:{key.Market}:{key.Symbol}".ToLowerInvariant();
        return Subscribe(name, () => new TickStreamSubscription<MarkPrice>(
            name,
            (handler, ct) => _source.SubscribeMarkPriceAsync(key, handler, ct),
            ct => _source.PollMarkPriceAsync(key, ct),
            _options.Value.Streams.MarkPricePollInterval,
            _options.Value.Streams, _time, _loggerFactory.CreateLogger<TickStreamSubscription<MarkPrice>>()), s => s.AddHandler(onMark));
    }

    private CandleStreamSubscription CreateCandleStream(MarketKey key, CandleInterval interval) =>
        new(key, interval, _source, _options.Value.Streams, _time, _loggerFactory.CreateLogger<CandleStreamSubscription>());

    public IReadOnlyList<StreamStatus> GetStatuses() =>
        _streams.Values.Select(s => s.Status).OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var name in _streams.Keys.ToArray())
        {
            if (_streams.TryRemove(name, out var entry))
                await entry.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IDisposable Subscribe<T>(string name, Func<StreamSubscriptionBase<T>> create, Func<StreamSubscriptionBase<T>, IDisposable> attach)
    {
        lock (_lock)
        {
            var entry = (StreamEntry<T>)_streams.GetOrAdd(name, _ =>
            {
                var subscription = create();
                _logger.LogInformation("Opening stream {Name}", name);
                return new StreamEntry<T>(subscription);
            });
            var registration = attach(entry.Subscription);
            entry.Subscription.Start();
            return new Unsubscriber(() => Release(name, entry, registration));
        }
    }

    private void Release<T>(string name, StreamEntry<T> entry, IDisposable registration)
    {
        StreamEntry<T>? toDispose = null;
        lock (_lock)
        {
            registration.Dispose();
            if (entry.Subscription.HandlerCount == 0 && _streams.TryRemove(new KeyValuePair<string, IStreamEntry>(name, entry)))
                toDispose = entry;
        }

        if (toDispose is not null)
        {
            _logger.LogInformation("Closing stream {Name}: no subscribers left", name);
            _ = toDispose.DisposeAsync().AsTask().ContinueWith(
                t => _logger.LogWarning(t.Exception, "Closing stream {Name} failed", name),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private interface IStreamEntry : IAsyncDisposable
    {
        StreamStatus Status { get; }
    }

    private sealed class StreamEntry<T>(StreamSubscriptionBase<T> subscription) : IStreamEntry
    {
        public StreamSubscriptionBase<T> Subscription { get; } = subscription;

        public StreamStatus Status => Subscription.Status;

        public ValueTask DisposeAsync() => Subscription.DisposeAsync();
    }

    private sealed class Unsubscriber(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                release();
        }
    }
}
