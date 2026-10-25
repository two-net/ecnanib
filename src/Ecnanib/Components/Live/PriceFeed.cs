using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Abstractions;

namespace Ecnanib.Components.Live;

/// <summary>
/// The last price of one market for an island: the mark price on futures (seeded from <see cref="IFuturesMetrics"/>),
/// the trade price on spot. The stream handler only stores the tick, so the hub's dispatcher is never slowed by a
/// render; a timer pushes the latest value at most once per <c>throttle</c>, and only when it changed.
/// </summary>
internal sealed class PriceFeed : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly IDisposable _subscription;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private decimal? _latest;
    private decimal? _pushed;

    public PriceFeed(IMarketStreams streams, IFuturesMetrics metrics, MarketKey key, TimeSpan throttle, Func<decimal, ValueTask> onPrice)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(onPrice);
        Key = key;
        if (key.Market == MarketType.UsdFutures)
        {
            _latest = metrics.Latest(key)?.Mark;
            try
            {
                _subscription = streams.SubscribeMarkPrice(key, (mark, _) => Set(mark.Mark));
            }
            catch (NotSupportedException)
            {
                _subscription = streams.SubscribePrice(key, (tick, _) => Set(tick.Price));
            }
        }
        else
        {
            _subscription = streams.SubscribePrice(key, (tick, _) => Set(tick.Price));
        }

        _timer = new PeriodicTimer(throttle);
        _pump = PumpAsync(onPrice);
    }

    public MarketKey Key { get; }

    /// <summary>The newest price seen, before the throttle; null until the first tick.</summary>
    public decimal? Latest
    {
        get
        {
            lock (_lock)
                return _latest;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await _cts.CancelAsync().ConfigureAwait(false);
        _timer.Dispose();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private ValueTask Set(decimal price)
    {
        lock (_lock)
            _latest = price;
        return ValueTask.CompletedTask;
    }

    private async Task PumpAsync(Func<decimal, ValueTask> onPrice)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                decimal? price;
                lock (_lock)
                {
                    if (_latest == _pushed)
                        continue;
                    price = _pushed = _latest;
                }

                if (price is { } changed)
                    await onPrice(changed).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            // The island is gone or failed to render; the next tick is not worth a crash of the pump.
        }
    }
}
