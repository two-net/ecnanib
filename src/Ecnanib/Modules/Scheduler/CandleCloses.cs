using Ecnanib.Kernel;

namespace Ecnanib.Modules.Scheduler;

/// <summary>
/// When each timeframe's candles close: aligned to UTC (FR-CYC-01), so 15m at :00, :15, :30 and :45, 1H every hour,
/// 4H at 00:00, 04:00 and so on to 20:00, and 1D at 00:00. Every interval divides a day, so counting from the Unix
/// epoch, which is a midnight, gives exactly those times.
/// </summary>
internal static class CandleCloses
{
    public static readonly Timeframe[] Timeframes = [Timeframe.FifteenMinutes, Timeframe.OneHour, Timeframe.FourHours, Timeframe.OneDay];

    public static TimeSpan Interval(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => TimeSpan.FromMinutes(15),
        Timeframe.OneHour => TimeSpan.FromHours(1),
        Timeframe.FourHours => TimeSpan.FromHours(4),
        Timeframe.OneDay => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null),
    };

    /// <summary>The name the configuration uses for <paramref name="timeframe"/>, as in SRS 7.3.</summary>
    public static string Label(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => "15m",
        Timeframe.OneHour => "1H",
        Timeframe.FourHours => "4H",
        Timeframe.OneDay => "1D",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null),
    };

    /// <summary>The last close at or before <paramref name="time"/>, in UTC.</summary>
    public static DateTimeOffset LatestAtOrBefore(Timeframe timeframe, DateTimeOffset time)
    {
        var interval = Interval(timeframe).Ticks;
        var sinceEpoch = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;

        return DateTimeOffset.UnixEpoch.AddTicks(sinceEpoch - Modulo(sinceEpoch, interval));
    }

    /// <summary>Never negative, unlike %, so a time before the epoch still rounds down.</summary>
    private static long Modulo(long value, long divisor) => ((value % divisor) + divisor) % divisor;
}
