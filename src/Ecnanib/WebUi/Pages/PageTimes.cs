using System.Globalization;

namespace Ecnanib.WebUi.Pages;

/// <summary>
/// How a page writes a time, in one place, so that every page writes it the same way. The times
/// are UTC, and say so.
/// </summary>
public static class PageTimes
{
    /// <summary>A moment as a person reads it, to the minute: <c>15 November 2026, 09:20 UTC</c>.</summary>
    public static string At(DateTimeOffset time) =>
        time.UtcDateTime.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>
    /// A moment to the second, for what happens within a minute, as the steps of a cycle do:
    /// <c>15 November 2026, 09:20:07 UTC</c>.
    /// </summary>
    public static string AtSecond(DateTimeOffset time) =>
        time.UtcDateTime.ToString("d MMMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>
    /// The time of day of a moment, to the minute, for what happens today and soon, as the end
    /// of a session does: <c>09:20 UTC</c>.
    /// </summary>
    public static string TimeOfDay(DateTimeOffset time) =>
        time.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>The day of a moment, in UTC: <c>15 November 2026</c>.</summary>
    public static string Day(DateTimeOffset time) =>
        time.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
}
