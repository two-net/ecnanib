using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.MarketData;

/// <summary>
/// A closed candle, as Binance sent it: SRS 6.1's CandleCache. Closed candles never change, so one fetched
/// is never fetched again while it's kept. Prices are in the symbol's quote asset and volume in its base
/// asset, which <c>exchange_symbols</c> names.
/// </summary>
internal sealed class CachedCandle
{
    /// <summary>The longest enum name stored here, with room to spare.</summary>
    public const int NameMaxLength = 16;

    public required Market Market { get; init; }

    public required TradingEnvironment Environment { get; init; }

    public required string Symbol { get; init; }

    public required CandleInterval Interval { get; init; }

    public required DateTimeOffset OpenTime { get; init; }

    public required decimal Open { get; init; }

    public required decimal High { get; init; }

    public required decimal Low { get; init; }

    public required decimal Close { get; init; }

    public required decimal Volume { get; init; }

    public required decimal QuoteVolume { get; init; }

    public required long Trades { get; init; }

    /// <summary>
    /// When it was fetched. Retention (#50) purges by this, not by open time, because a daily window spans
    /// more than a year: a purged candle is fetched again with its window.
    /// </summary>
    public required DateTimeOffset FetchedAt { get; init; }

    public Candle ToCandle() => new(OpenTime, Open, High, Low, Close, Volume, QuoteVolume, Trades);
}

internal sealed class CachedCandleConfiguration : IEntityTypeConfiguration<CachedCandle>
{
    public void Configure(EntityTypeBuilder<CachedCandle> builder)
    {
        builder.ToTable("candle_cache");

        // Also the index a window is read by: one series, by open time.
        builder.HasKey(candle => new { candle.Market, candle.Environment, candle.Symbol, candle.Interval, candle.OpenTime });
        builder.Property(candle => candle.Market).HasConversion<string>().HasMaxLength(CachedCandle.NameMaxLength);
        builder.Property(candle => candle.Environment).HasConversion<string>().HasMaxLength(CachedCandle.NameMaxLength);
        builder.Property(candle => candle.Interval).HasConversion<string>().HasMaxLength(CachedCandle.NameMaxLength);
        builder.Property(candle => candle.Symbol).HasMaxLength(ExchangeSymbol.SymbolMaxLength);
        builder.HasIndex(candle => candle.FetchedAt);
    }
}
