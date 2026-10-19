using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Candle = Ecnanib.Exchange.Abstractions.Candle;
using StoredCandle = Ecnanib.Database.Entities.Candle;

namespace Ecnanib.Agents.Services;

/// <summary>Writes closed candles into <c>candles</c> for audit (FR-DATA-05); a candle already there is left alone.</summary>
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
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
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
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another evaluation stored some of them meanwhile; one by one settles it.
            db.ChangeTracker.Clear();
            var added = 0;
            foreach (var candle in missing)
                added += await StoreAsync(key, interval, candle, cancellationToken).ConfigureAwait(false) ? 1 : 0;
            return added;
        }
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

    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
