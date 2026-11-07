using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;

namespace Ecnanib.Modules.MarketData;

/// <summary>
/// One loop per watched market, environment and pair, which reads its price, and the latest two candles of each
/// timeframe a watcher asked for, every <see cref="MarketDataSettings.LiveMarketInterval"/>.
/// <list type="bullet">
/// <item>A loop starts with its first watcher and stops when its last one leaves, so a page nobody has open costs
/// nothing. A new timeframe wakes it, so its first candle comes at once.</item>
/// <item>It reads only while the pair's server has used less than half of what normal requests may, and never during
/// a backoff or ban, so a page can never take weight from a cycle or protective work. A read that fails waits twice as
/// long each time, up to a minute, or as long as Binance said.</item>
/// <item>Nothing is stored: a read is sent to the watchers and kept in memory for the next one to join.</item>
/// </list>
/// See Live market data in docs/architecture.md.
/// </summary>
internal sealed class MarketWatch(
    IExchangeMarketData exchange,
    IExchangeUsage usage,
    MarketDataSettings settings,
    TimeProvider time,
    ILogger<MarketWatch> logger) : IMarketWatch, IDisposable
{
    /// <summary>The most pairs watched at once, so pages can't add up to more weight than the server can spare.</summary>
    public const int MaximumStreams = 50;

    /// <summary>The longest a loop waits after failures, unless Binance says to wait longer.</summary>
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<StreamKey, Stream> _streams = [];
    private bool _disposed;

    public IDisposable Watch(WatchKey key, Action<LiveMarket> onUpdate)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(onUpdate);

        if (!Failures.IsSymbol(key.Symbol))
        {
            throw new ArgumentException("A symbol is upper-case letters and digits, such as BTCUSDT.", nameof(key));
        }

        var streamKey = new StreamKey(key.Market, key.Environment, key.Symbol);
        var watcher = new Watcher(key, onUpdate);
        LiveMarket? latest = null;
        Stream? stream;

        lock (_lock)
        {
            if (_disposed)
            {
                return Subscription.None;
            }

            if (!_streams.TryGetValue(streamKey, out stream))
            {
                if (_streams.Count >= MaximumStreams)
                {
                    logger.LogWarning(
                        "{Market} {Environment} {Symbol} isn't watched, because {Count} pairs already are", key.Market, key.Environment, key.Symbol, _streams.Count);
                    return Subscription.None;
                }

                stream = new Stream(streamKey);
                _streams[streamKey] = stream;
                stream.Watchers.Add(watcher);
                _ = RunAsync(stream);
            }
            else
            {
                var newTimeframe = key.Timeframe is { } timeframe && !stream.Watchers.Any(other => other.Key.Timeframe == timeframe);
                stream.Watchers.Add(watcher);
                latest = stream.LatestFor(key);

                if (newTimeframe)
                {
                    stream.Wake();
                }
            }
        }

        if (latest is not null)
        {
            Send(watcher, latest);
        }

        return new Subscription(() => Leave(stream, watcher));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;

            foreach (var stream in _streams.Values)
            {
                stream.Stop();
            }

            _streams.Clear();
        }
    }

    private void Leave(Stream stream, Watcher watcher)
    {
        lock (_lock)
        {
            stream.Watchers.Remove(watcher);

            if (stream.Watchers.Count == 0 && _streams.TryGetValue(stream.Key, out var current) && current == stream)
            {
                _streams.Remove(stream.Key);
                stream.Stop();
            }
        }
    }

    private async Task RunAsync(Stream stream)
    {
        // So that Watch only starts the loop, under its lock, and never runs it.
        await Task.Yield();

        var failures = 0;

        while (!stream.Stopping.IsCancellationRequested)
        {
            var wait = settings.LiveMarketInterval;

            try
            {
                if (Busy(stream.Key) is { } until)
                {
                    Publish(stream, stale: true);
                    wait = Max(wait, until - time.GetUtcNow());
                }
                else
                {
                    await ReadAsync(stream);
                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (stream.Stopping.IsCancellationRequested)
            {
                break;
            }
            catch (ExchangeException exception)
            {
                failures++;
                wait = exception.RetryAfter is { } after ? Max(after, wait) : Backoff(failures);
                logger.LogInformation(
                    "{Market} {Environment} {Symbol} couldn't be read for a page, so it's read again in {Wait}: {Failure}",
                    stream.Key.Market,
                    stream.Key.Environment,
                    stream.Key.Symbol,
                    wait,
                    exception.Failure);
                Publish(stream, stale: true);
            }
            catch (Exception exception)
            {
                failures++;
                wait = Backoff(failures);
                logger.LogWarning(exception, "Reading {Market} {Environment} {Symbol} for a page failed", stream.Key.Market, stream.Key.Environment, stream.Key.Symbol);
                Publish(stream, stale: true);
            }

            try
            {
                await stream.WaitAsync(wait, time);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// When the pair's server can't spare the weight, until when to wait, or null to read now. Normal requests may use
    /// the limit less the headroom, and a page takes only from the first half of that.
    /// </summary>
    private DateTimeOffset? Busy(StreamKey key)
    {
        var server = ExchangeServers.Public(key.Market, key.Environment);
        var now = time.GetUtcNow();

        if (usage.Current().FirstOrDefault(current => current.Server == server) is not { } current)
        {
            return null;
        }

        if (current.BannedUntil is { } banned && banned > now)
        {
            return banned;
        }

        if (current.BackoffUntil is { } backoff && backoff > now)
        {
            return backoff;
        }

        var normal = current.WeightLimit * (100 - current.HeadroomPercent) / 100;

        return current.UsedWeight * 2 >= normal ? now + settings.LiveMarketInterval : null;
    }

    /// <summary>The price, and the latest two candles of each watched timeframe, all at once.</summary>
    private async Task ReadAsync(Stream stream)
    {
        Timeframe[] timeframes;

        lock (_lock)
        {
            timeframes = [.. stream.Watchers.Select(watcher => watcher.Key.Timeframe).OfType<Timeframe>().Distinct()];
        }

        var (market, environment, symbol) = stream.Key;
        var cancellationToken = stream.Stopping;
        var now = time.GetUtcNow();

        var premium = market == Market.Futures ? exchange.GetPremiumIndexAsync(environment, symbol, cancellationToken) : null;
        var quote = market == Market.Spot ? exchange.GetQuoteAsync(market, environment, symbol, cancellationToken) : null;
        var candles = timeframes.ToDictionary(
            timeframe => timeframe,
            timeframe => exchange.GetCandlesAsync(market, environment, symbol, Timeframes.Interval(timeframe), null, now, 2, cancellationToken));

        await Task.WhenAll(candles.Values.Cast<Task>().Append(premium ?? Task.CompletedTask).Append(quote ?? Task.CompletedTask));

        var index = premium is null ? null : await premium;
        var book = quote is null ? null : await quote;
        var at = time.GetUtcNow();
        var reads = new Dictionary<Timeframe, (Candle? Forming, Candle? LastClosed)>();

        foreach (var (timeframe, read) in candles)
        {
            var fetched = await read;
            var forming = fetched.LastOrDefault(candle => candle.CloseTime >= at);
            var closed = fetched.LastOrDefault(candle => candle.CloseTime < at);
            reads[timeframe] = (forming is null ? null : ToCandle(forming), closed is null ? null : ToCandle(closed));
        }

        lock (_lock)
        {
            stream.Latest = new Read(index?.MarkPrice, book?.LastPrice, book?.BidPrice, book?.AskPrice, reads, at);
        }

        Publish(stream, stale: false);
    }

    /// <summary>The latest read to each watcher, marked stale when this pass couldn't read.</summary>
    private void Publish(Stream stream, bool stale)
    {
        List<(Watcher Watcher, LiveMarket Market)> sends;

        lock (_lock)
        {
            sends = [.. stream.Watchers.Select(watcher => (watcher, stream.LatestFor(watcher.Key, stale) ?? Empty(watcher.Key, stale)))];
        }

        foreach (var (watcher, market) in sends)
        {
            Send(watcher, market);
        }
    }

    private LiveMarket Empty(WatchKey key, bool stale) => new(key, null, null, null, null, null, null, time.GetUtcNow(), stale);

    /// <summary>One watcher that throws can't stop the loop, or the others.</summary>
    private void Send(Watcher watcher, LiveMarket market)
    {
        try
        {
            watcher.OnUpdate(market);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A page watching {Symbol} failed to take its update", market.Key.Symbol);
        }
    }

    private TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, settings.LiveMarketInterval.Ticks << Math.Clamp(failures, 0, 6)));

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    private static Candle ToCandle(ExchangeCandle candle) =>
        new(candle.OpenTime, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, candle.QuoteVolume, candle.Trades);

    private sealed record StreamKey(Market Market, TradingEnvironment Environment, string Symbol);

    private sealed record Watcher(WatchKey Key, Action<LiveMarket> OnUpdate);

    /// <summary>One pass's figures, shared by every watcher of the pair.</summary>
    private sealed record Read(
        decimal? MarkPrice,
        decimal? LastPrice,
        decimal? BidPrice,
        decimal? AskPrice,
        IReadOnlyDictionary<Timeframe, (Candle? Forming, Candle? LastClosed)> Candles,
        DateTimeOffset At);

    /// <summary>One loop's state. Its watchers and latest read are guarded by the watch's lock.</summary>
    private sealed class Stream(StreamKey key)
    {
        private readonly CancellationTokenSource _stopping = new();
        private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StreamKey Key { get; } = key;

        public List<Watcher> Watchers { get; } = [];

        public Read? Latest { get; set; }

        public CancellationToken Stopping => _stopping.Token;

        public LiveMarket? LatestFor(WatchKey key, bool stale = false)
        {
            if (Latest is not { } read)
            {
                return null;
            }

            var (forming, closed) = key.Timeframe is { } timeframe ? read.Candles.GetValueOrDefault(timeframe) : default;
            var last = read.LastPrice ?? forming?.Close;

            return new LiveMarket(key, forming, closed, last, read.MarkPrice, read.BidPrice, read.AskPrice, read.At, stale);
        }

        public void Wake() => _wake.TrySetResult();

        public void Stop()
        {
            _stopping.Cancel();
            _wake.TrySetResult();
        }

        /// <summary>Until <paramref name="wait"/> has passed, the loop is woken, or it stops.</summary>
        /// <exception cref="OperationCanceledException">It stopped.</exception>
        public async Task WaitAsync(TimeSpan wait, TimeProvider time)
        {
            var wake = _wake.Task;
            var delay = Task.Delay(wait, time, Stopping);

            await Task.WhenAny(delay, wake);

            if (wake.IsCompleted)
            {
                Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            Stopping.ThrowIfCancellationRequested();
        }
    }

    private sealed class Subscription(Action? leave) : IDisposable
    {
        public static Subscription None { get; } = new(null);

        private Action? _leave = leave;

        public void Dispose() => Interlocked.Exchange(ref _leave, null)?.Invoke();
    }
}
