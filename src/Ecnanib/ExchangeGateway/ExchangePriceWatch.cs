using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// Reads each price that a page follows, once a second for all who follow it, while the limit
/// of its market has room. See <see cref="IExchangePriceWatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It waits a second after each read, and not a second from its start.</b> What a read gave
/// counts for a second with the market data, so a read that came a moment early would be
/// answered with the price of the read before it.
/// </para>
/// <para>
/// <b>A read that gives no price is left at that.</b> The exchange did not answer, the limit
/// has no room, or the symbol is one that the configuration file no longer lists, which the
/// position of an agent may still be in. The next second asks again.
/// </para>
/// </remarks>
internal sealed class ExchangePriceWatch(
    IExchangeMarketData marketData,
    IExchangeLimits limits,
    TimeProvider clock,
    ILogger<ExchangePriceWatch> logger) : IExchangePriceWatch, IDisposable
{
    /// <summary>How long the watch waits between two reads of a price. It is fixed, and is no setting.</summary>
    public static readonly TimeSpan ReadsEvery = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<Watched, Watch> _watches = [];
    private readonly CancellationTokenSource _stop = new();

    public IDisposable Follow(Market market, TradingMode mode, string symbol, ExchangePriceKind kind, Action<WatchedPrice> heard)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        ArgumentNullException.ThrowIfNull(heard);

        if (kind == ExchangePriceKind.Mark && market != Market.Futures)
        {
            throw new ArgumentException("Only the Futures market has a mark price.", nameof(kind));
        }

        var watched = new Watched(market, mode, symbol, kind);
        Follower follower;
        WatchedPrice? latest;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);

            if (!_watches.TryGetValue(watched, out var watch))
            {
                watch = new Watch(watched);
                _watches[watched] = watch;

                // Nothing is read that nobody follows: the reads begin with the first follower.
                // They are for everyone who follows the price, so nothing of the connection
                // that they happened to begin with flows into what they log.
                using (ExecutionContext.SuppressFlow())
                {
                    _ = Task.Run(() => ReadAsync(watch, _stop.Token));
                }
            }

            follower = new Follower(this, watch, heard);
            watch.Followers.Add(follower);
            latest = watch.Latest;
        }

        if (latest is not null)
        {
            Tell(follower, latest);
        }

        return follower;
    }

    public WatchedPrice? Latest(Market market, TradingMode mode, string symbol, ExchangePriceKind kind)
    {
        lock (_gate)
        {
            return _watches.TryGetValue(new Watched(market, mode, symbol, kind), out var watch) ? watch.Latest : null;
        }
    }

    private async Task ReadAsync(Watch watch, CancellationToken stop)
    {
        // Set by the first report of a read that failed with an error, so that it is not reported every second.
        var troubled = false;

        while (true)
        {
            lock (_gate)
            {
                if (watch.Followers.Count == 0)
                {
                    // Nobody follows any more. Whoever follows next begins anew.
                    _watches.Remove(watch.Of);

                    return;
                }
            }

            try
            {
                if (limits.HasRoom(watch.Of.Market, watch.Of.Mode) && await OnceAsync(watch.Of, stop) is { } read)
                {
                    Follower[] told;

                    lock (_gate)
                    {
                        watch.Latest = read;
                        told = [.. watch.Followers];
                    }

                    foreach (var follower in told)
                    {
                        Tell(follower, read);
                    }
                }

                await Task.Delay(ReadsEvery, clock, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The application is stopping.
                return;
            }
            catch (Exception error)
            {
                if (!troubled)
                {
                    troubled = true;

                    // A symbol that the configuration file no longer lists, for one.
                    logger.LogWarning(
                        error,
                        "The price of {Symbol} on {Market} in {TradingMode} could not be asked for, so the pages that follow it keep the price that they have",
                        watch.Of.Symbol,
                        watch.Of.Market,
                        watch.Of.Mode);
                }

                try
                {
                    await Task.Delay(ReadsEvery, clock, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One read of a price, or null when the exchange gave none.</summary>
    private async Task<WatchedPrice?> OnceAsync(Watched watched, CancellationToken stop)
    {
        if (watched.Kind == ExchangePriceKind.Mark)
        {
            var mark = await marketData.GetMarkPriceAsync(watched.Mode, watched.Symbol, stop);

            return mark is { Outcome: ExchangeCallOutcome.Succeeded, MarkPrice: { } price, ReadAt: { } readAt } ? new WatchedPrice(price, readAt) : null;
        }

        var last = await marketData.GetPriceAsync(watched.Market, watched.Mode, watched.Symbol, stop);

        return last is { Outcome: ExchangeCallOutcome.Succeeded, Price: { } traded, ReadAt: { } at } ? new WatchedPrice(traded, at) : null;
    }

    private void Tell(Follower follower, WatchedPrice price)
    {
        try
        {
            follower.Heard(price);
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "What follows the price of {Symbol} on {Market} in {TradingMode} failed on a price. The price reached nothing there",
                follower.Watch.Of.Symbol,
                follower.Watch.Of.Market,
                follower.Watch.Of.Mode);
        }
    }

    private void Unfollow(Follower follower)
    {
        lock (_gate)
        {
            follower.Watch.Followers.Remove(follower);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
        }
    }

    /// <summary>A price that is followed: which one, of which symbol, where.</summary>
    private sealed record Watched(Market Market, TradingMode Mode, string Symbol, ExchangePriceKind Kind);

    /// <summary>The followers of one price, and what was last read of it.</summary>
    private sealed class Watch(Watched of)
    {
        public Watched Of { get; } = of;

        public List<Follower> Followers { get; } = [];

        public WatchedPrice? Latest { get; set; }
    }

    /// <summary>What follows a price, until it is disposed of.</summary>
    private sealed class Follower(ExchangePriceWatch watching, Watch watch, Action<WatchedPrice> heard) : IDisposable
    {
        public Watch Watch { get; } = watch;

        public Action<WatchedPrice> Heard { get; } = heard;

        public void Dispose() => watching.Unfollow(this);
    }
}
