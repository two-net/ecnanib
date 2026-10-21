using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.MarketData.Services;

/// <summary>
/// Follows the mark-price stream of every futures symbol with a lease and keeps the latest value (FR-DATA-04). Leases
/// are counted per key; the last release closes the stream. A stream that cannot be opened is logged once and leaves
/// the value null, so the loader reads the mark price on demand instead.
/// </summary>
internal sealed class FuturesMetrics : IFuturesMetrics
{
    private readonly IMarketStreams _streams;
    private readonly ILogger<FuturesMetrics> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<MarketKey, Lease> _leases = [];

    public FuturesMetrics(IMarketStreams streams, ILogger<FuturesMetrics> logger)
    {
        _streams = streams;
        _logger = logger;
    }

    public MarkPrice? Latest(MarketKey key)
    {
        lock (_lock)
            return _leases.TryGetValue(key, out var lease) ? lease.Latest : null;
    }

    /// <summary>How many keys are followed right now.</summary>
    internal int Followed
    {
        get
        {
            lock (_lock)
                return _leases.Count;
        }
    }

    /// <summary>Follows the symbol's mark price until the returned handle is disposed.</summary>
    public IDisposable Acquire(MarketKey key)
    {
        if (key.Market != MarketType.UsdFutures)
            throw new ArgumentException($"{key} is not a futures key.", nameof(key));

        lock (_lock)
        {
            if (!_leases.TryGetValue(key, out var lease))
            {
                lease = new Lease();
                _leases[key] = lease;
                lease.Subscription = Subscribe(key, lease);
                _logger.LogInformation("Mark price of {Key} is followed", key);
            }

            lease.Users++;
            return new Releaser(() => Release(key));
        }
    }

    private IDisposable? Subscribe(MarketKey key, Lease lease)
    {
        try
        {
            return _streams.SubscribeMarkPrice(key, (mark, _) =>
            {
                lease.Latest = mark;
                return ValueTask.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The mark price stream of {Key} could not be opened; the mark price is read on demand instead", key);
            return null;
        }
    }

    private void Release(MarketKey key)
    {
        IDisposable? subscription;
        lock (_lock)
        {
            if (!_leases.TryGetValue(key, out var lease) || --lease.Users > 0)
                return;
            _leases.Remove(key);
            subscription = lease.Subscription;
        }

        subscription?.Dispose();
        _logger.LogInformation("Mark price of {Key} is no longer followed", key);
    }

    private sealed class Lease
    {
        public int Users;

        public IDisposable? Subscription;

        public volatile MarkPrice? Latest;
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release();
        }
    }
}
