using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// When a "no new entry" window is active (FR-STR-05). Start is inclusive and end exclusive; a recurring window may wrap
/// past the end of the week (Friday 21:00 → Monday 01:00); one whose start equals its end is empty. Everything is UTC.
/// </summary>
public static class EntryWindowSchedule
{
    private const int SecondsPerDay = 86_400;

    public static bool AnyActive(IEnumerable<EntryWindow> windows, DateTimeOffset utc) => windows.Any(w => IsActive(w, utc));

    public static bool IsActive(EntryWindow window, DateTimeOffset utc)
    {
        ArgumentNullException.ThrowIfNull(window);
        switch (window.Kind)
        {
            case EntryWindowKind.OneOff:
                return window.StartAt is { } start && window.EndAt is { } end && start <= utc && utc < end;
            case EntryWindowKind.Recurring:
                if (window.StartDay is not { } startDay || window.StartTime is not { } startTime || window.EndDay is not { } endDay || window.EndTime is not { } endTime)
                    return false;
                var s = WeekSecond(startDay, startTime);
                var e = WeekSecond(endDay, endTime);
                var t = WeekSecond(utc);
                return s < e ? t >= s && t < e : s > e && (t >= s || t < e);
            default:
                return false;
        }
    }

    /// <summary>Seconds since Monday 00:00 UTC; the week arithmetic of recurring windows.</summary>
    public static int WeekSecond(DayOfWeek day, TimeOnly time) => ((int)day + 6) % 7 * SecondsPerDay + (int)time.ToTimeSpan().TotalSeconds;

    public static int WeekSecond(DateTimeOffset utc)
    {
        var time = utc.UtcDateTime;
        return WeekSecond(time.DayOfWeek, TimeOnly.FromTimeSpan(time.TimeOfDay));
    }
}
