using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.MarketData;

/// <summary>One symbol's candles of one length, on one market and environment.</summary>
internal sealed record SeriesKey(Market Market, TradingEnvironment Environment, string Symbol, CandleInterval Interval);

/// <summary>
/// Closed candles, fetched once per series and candle, and shared by every agent that asks (FR-CYC-09).
/// <list type="bullet">
/// <item>Memory holds one load per series, for its latest candle. Callers asking for that candle wait for
/// it, a newer candle replaces it, and an older one, such as a catch-up cycle's, loads on its own. A load
/// that failed is replaced by the next caller's.</item>
/// <item>A load runs with no caller's token, so a caller that stops waiting can't cancel it for the
/// others. The adapter's read timeout and the database's command timeout bound it.</item>
/// <item>Loads of one series run one at a time, so two can't write the same rows.</item>
/// <item><c>candle_cache</c> keeps what was fetched, so a load asks Binance only for the candles it
/// doesn't have: usually just the newest one, and the whole window only when the start is missing.</item>
/// </list>
/// A shared load logs under the scope of the caller that started it. See Market data in docs/architecture.md.
/// </summary>
internal sealed class CandleFeed(
    IExchangeMarketData exchange,
    IDbContextFactory<EcnanibDbContext> contexts,
    TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<SeriesKey, Load> _latest = [];
    private readonly ConcurrentDictionary<SeriesKey, SemaphoreSlim> _gates = new();

    /// <summary>
    /// Where Binance's candles begin, for a series whose last full fetch came back short: a symbol newer
    /// than the window, or a gap at its start. Without it, such a series would fetch its whole window
    /// every time. It's only memory, so a restart costs one full fetch per series.
    /// </summary>
    private readonly ConcurrentDictionary<SeriesKey, DateTimeOffset> _starts = new();

    /// <summary>The window of closed candles ending with the one that opened at <paramref name="lastOpen"/>.</summary>
    /// <exception cref="ExchangeException">Binance couldn't provide what's missing.</exception>
    public Task<CandleSeries> GetAsync(SeriesKey key, DateTimeOffset lastOpen, CancellationToken cancellationToken)
    {
        Task<CandleSeries> load;

        lock (_lock)
        {
            if (_latest.TryGetValue(key, out var latest) && latest.LastOpen == lastOpen && !latest.Task.IsFaulted && !latest.Task.IsCanceled)
            {
                load = latest.Task;
            }
            else
            {
                load = LoadAsync(key, lastOpen);

                if (latest is null || latest.LastOpen <= lastOpen)
                {
                    _latest[key] = new Load(lastOpen, load);
                }
            }
        }

        return load.WaitAsync(cancellationToken);
    }

    private async Task<CandleSeries> LoadAsync(SeriesKey key, DateTimeOffset lastOpen)
    {
        // So that the lock in GetAsync is held only to store the task, not to run it.
        await Task.Yield();

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None);

        try
        {
            var length = Timeframes.Length(key.Interval);
            var first = lastOpen - (length * (CandleSeries.Window - 1));
            var candles = await ReadAsync(key, first, lastOpen);

            if (Missing(key, candles, first, lastOpen, length) is { } missing)
            {
                var fetched = await exchange.GetCandlesAsync(
                    key.Market, key.Environment, key.Symbol, key.Interval, missing.From, lastOpen, missing.Limit, CancellationToken.None);

                // Binance applies the range too, but a candle still open is never kept.
                var now = time.GetUtcNow();
                var closed = fetched.Where(candle => candle.OpenTime >= missing.From && candle.OpenTime <= lastOpen && candle.CloseTime < now).ToList();

                if (missing.From == first && closed.Count < CandleSeries.Window)
                {
                    _starts[key] = closed.Count > 0 ? closed[0].OpenTime : lastOpen + length;
                }

                await StoreAsync(key, missing.From, lastOpen, closed, now);
                candles = Merge(candles, closed);
            }

            return new CandleSeries(length, lastOpen, candles, Indicators.Compute(candles));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// What to ask Binance for, or null when the cache has the window. The cached candles are all inside
    /// the window, so the newer ones missing always fit in one request.
    /// </summary>
    private (DateTimeOffset From, int Limit)? Missing(SeriesKey key, List<Candle> candles, DateTimeOffset first, DateTimeOffset lastOpen, TimeSpan length)
    {
        var hasStart = candles.Count > 0
            && (candles[0].OpenTime <= first || (_starts.TryGetValue(key, out var start) && candles[0].OpenTime <= start));

        if (!hasStart)
        {
            return (first, CandleSeries.Window);
        }

        var newest = candles[^1].OpenTime;

        return newest == lastOpen ? null : (newest + length, (int)((lastOpen - newest) / length));
    }

    private async Task<List<Candle>> ReadAsync(SeriesKey key, DateTimeOffset first, DateTimeOffset lastOpen)
    {
        await using var context = await contexts.CreateDbContextAsync(CancellationToken.None);

        var rows = await Series(context, key)
            .Where(candle => candle.OpenTime >= first && candle.OpenTime <= lastOpen)
            .OrderBy(candle => candle.OpenTime)
            .AsNoTracking()
            .ToListAsync(CancellationToken.None);

        return [.. rows.Select(row => row.ToCandle())];
    }

    /// <summary>Replaces the fetched range in one transaction, so a reader sees all of it or none.</summary>
    private async Task StoreAsync(SeriesKey key, DateTimeOffset from, DateTimeOffset to, List<ExchangeCandle> fetched, DateTimeOffset now)
    {
        if (fetched.Count == 0)
        {
            return;
        }

        await using var context = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        await Series(context, key).Where(candle => candle.OpenTime >= from && candle.OpenTime <= to).ExecuteDeleteAsync(CancellationToken.None);
        context.AddRange(fetched.Select(candle => new CachedCandle
        {
            Market = key.Market,
            Environment = key.Environment,
            Symbol = key.Symbol,
            Interval = key.Interval,
            OpenTime = candle.OpenTime,
            Open = candle.Open,
            High = candle.High,
            Low = candle.Low,
            Close = candle.Close,
            Volume = candle.Volume,
            QuoteVolume = candle.QuoteVolume,
            Trades = candle.Trades,
            FetchedAt = now,
        }));
        await context.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static IQueryable<CachedCandle> Series(EcnanibDbContext context, SeriesKey key) =>
        context.Set<CachedCandle>().Where(candle =>
            candle.Market == key.Market
            && candle.Environment == key.Environment
            && candle.Symbol == key.Symbol
            && candle.Interval == key.Interval);

    /// <summary>The cached candles with the fetched ones in their place, oldest first.</summary>
    private static List<Candle> Merge(List<Candle> cached, List<ExchangeCandle> fetched)
    {
        var byOpenTime = cached.ToDictionary(candle => candle.OpenTime);

        foreach (var candle in fetched)
        {
            byOpenTime[candle.OpenTime] = new Candle(
                candle.OpenTime, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, candle.QuoteVolume, candle.Trades);
        }

        return [.. byOpenTime.Values.OrderBy(candle => candle.OpenTime)];
    }

    private sealed record Load(DateTimeOffset LastOpen, Task<CandleSeries> Task);
}
