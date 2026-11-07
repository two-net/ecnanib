using Ecnanib.Kernel;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// UTC days, and when a day's metrics are final. A cycle counts on the day it started, as budgets and retention count
/// it. Nothing about a call changes once it's recorded, so a day is final once its last cycle has finished and its
/// analysts' last calls can be scored: the day's end, plus the horizon in candles, plus <see cref="Slack"/>. Every
/// cycle deadline is shorter than its candle, so the horizon covers that too.
/// </summary>
internal static class ExperienceDays
{
    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>For the cycle that closes the horizon to read its candle, and for any clock drift.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a final day whose prices couldn't all be read is built again, before it's kept as it is with those
    /// calls unscored.
    /// </summary>
    public static readonly TimeSpan PriceGrace = TimeSpan.FromDays(1);

    public static TimeSpan Interval(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => TimeSpan.FromMinutes(15),
        Timeframe.OneHour => TimeSpan.FromHours(1),
        Timeframe.FourHours => TimeSpan.FromHours(4),
        Timeframe.OneDay => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null),
    };

    /// <summary>UTC midnight at the start of the day <paramref name="time"/> falls in.</summary>
    public static DateTimeOffset Start(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();

        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    public static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>When the day starting at <paramref name="day"/> becomes final for <paramref name="timeframe"/>.</summary>
    public static DateTimeOffset FinalAt(DateTimeOffset day, Timeframe timeframe, int horizon) =>
        day + Day + (Interval(timeframe) * horizon) + Slack;

    /// <summary>The end of the last day that's final at <paramref name="now"/>: every day before it is.</summary>
    public static DateTimeOffset FinalUntil(Timeframe timeframe, int horizon, DateTimeOffset now) =>
        Start(now - (Interval(timeframe) * horizon) - Slack);
}
