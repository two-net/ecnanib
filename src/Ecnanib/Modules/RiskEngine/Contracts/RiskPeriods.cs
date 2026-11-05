namespace Ecnanib.Modules.RiskEngine.Contracts;

/// <summary>
/// The UTC day and week that the loss limits count over and pause until (FR-RISK-02, FR-RISK-08). A week starts
/// on Monday at 00:00 UTC, as ISO 8601's does. Whoever sums an agent's P&amp;L for <see cref="TradingRecord"/>
/// uses these, so its window is the engine's.
/// </summary>
public static class RiskPeriods
{
    // Whole days as TimeSpans, because AddDays takes a double, which the engine never uses (DATA-R-02).
    private static readonly TimeSpan Day = new(1, 0, 0, 0);

    private static readonly TimeSpan Week = new(7, 0, 0, 0);

    /// <summary>00:00 UTC of the day <paramref name="now"/> falls in.</summary>
    public static DateTimeOffset DayStart(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;

        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>00:00 UTC of the next day.</summary>
    public static DateTimeOffset NextDay(DateTimeOffset now) => DayStart(now) + Day;

    /// <summary>Monday 00:00 UTC of the week <paramref name="now"/> falls in.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        var day = DayStart(now);

        // Monday is 1 and Sunday 0, so Sunday is the week's seventh day.
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;

        return day - new TimeSpan(sinceMonday, 0, 0, 0);
    }

    /// <summary>Monday 00:00 UTC of the next week.</summary>
    public static DateTimeOffset NextWeek(DateTimeOffset now) => WeekStart(now) + Week;
}
