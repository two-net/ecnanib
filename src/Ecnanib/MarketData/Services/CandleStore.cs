using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;
using Candle = Ecnanib.Exchange.Abstractions.Candle;
using StoredCandle = Ecnanib.Database.Entities.Candle;

namespace Ecnanib.MarketData.Services;

/// <summary>Writes closed candles into <c>candles</c> for audit (FR-DATA-05); a candle already there is left alone. The retention sweep deletes them after <c>Retention:CandleDays</c>.</summary>
internal sealed class CandleStore
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public CandleStore(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>Returns true when the candle was inserted, false when it was already stored.</summary>
    public async Task<bool> StoreAsync(MarketKey key, CandleInterval interval, Candle candle, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Candles.Add(ToEntity(key, interval, candle));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return false;
        }
    }

    /// <summary>Inserts the candles not yet stored; returns how many were added.</summary>
    public async Task<int> StoreManyAsync(MarketKey key, CandleInterval interval, IReadOnlyList<Candle> candles, CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
            return 0;

        var from = candles.Min(c => c.OpenTime);
        var to = candles.Max(c => c.OpenTime);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Candles
            .Where(c => c.Environment == key.Environment && c.MarketType == key.Market && c.Symbol == key.Symbol && c.Interval == interval && c.OpenTime >= from && c.OpenTime <= to)
            .Select(c => c.OpenTime)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var known = existing.ToHashSet();
        var missing = candles.Where(c => !known.Contains(c.OpenTime)).ToList();
        if (missing.Count == 0)
            return 0;

        db.Candles.AddRange(missing.Select(c => ToEntity(key, interval, c)));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return missing.Count;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Another evaluation stored some of them meanwhile; one by one settles it.
            db.ChangeTracker.Clear();
            var added = 0;
            foreach (var candle in missing)
                added += await StoreAsync(key, interval, candle, cancellationToken).ConfigureAwait(false) ? 1 : 0;
            return added;
        }
    }

    /// <summary>The newest <paramref name="count"/> stored candles of a series, ascending; every stored candle is closed.</summary>
    public async Task<IReadOnlyList<Candle>> ReadRecentAsync(MarketKey key, CandleInterval interval, int count, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Candles.AsNoTracking()
            .Where(c => c.Environment == key.Environment && c.MarketType == key.Market && c.Symbol == key.Symbol && c.Interval == interval)
            .OrderByDescending(c => c.OpenTime)
            .Take(count)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        rows.Reverse();
        return rows.Select(c => new Candle(c.Symbol, c.Interval, c.OpenTime, c.CloseTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.QuoteVolume, c.TradeCount, true)).ToList();
    }

    private static StoredCandle ToEntity(MarketKey key, CandleInterval interval, Candle candle) => new()
    {
        Environment = key.Environment,
        MarketType = key.Market,
        Symbol = key.Symbol,
        Interval = interval,
        OpenTime = candle.OpenTime,
        CloseTime = candle.CloseTime,
        Open = candle.Open,
        High = candle.High,
        Low = candle.Low,
        Close = candle.Close,
        Volume = candle.Volume,
        QuoteVolume = candle.QuoteVolume,
        TradeCount = candle.TradeCount,
    };
}
