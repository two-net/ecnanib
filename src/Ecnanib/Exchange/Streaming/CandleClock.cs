using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Streaming;

/// <summary>UTC-aligned candle boundaries (FR-AGT-09). Weekly candles open on Monday 00:00 UTC, as on Binance.</summary>
internal static class CandleClock
{
    /// <summary>1970-01-05 is the first Monday after the Unix epoch (which was a Thursday).</summary>
    private static readonly DateTimeOffset WeekEpoch = new(1970, 1, 5, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset OpenTimeOf(DateTimeOffset time, CandleInterval interval)
    {
        var span = interval.ToTimeSpan();
        var epoch = interval == CandleInterval.OneWeek ? WeekEpoch : DateTimeOffset.UnixEpoch;
        var elapsed = (time.ToUniversalTime() - epoch).Ticks;
        var remainder = elapsed % span.Ticks;
        if (remainder < 0)
            remainder += span.Ticks;
        return epoch + TimeSpan.FromTicks(elapsed - remainder);
    }

    public static DateTimeOffset NextCloseAfter(DateTimeOffset time, CandleInterval interval) =>
        OpenTimeOf(time, interval) + interval.ToTimeSpan();
}
