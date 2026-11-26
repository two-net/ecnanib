using System.Globalization;
using Ecnanib.WebUi.TimeZones;

namespace Ecnanib.WebUi.Pages;

/// <summary>
/// How a page writes a time, in one place, so that every page writes it the same way: in the
/// time zone of the page, which is the local time zone of its user, or UTC where the user chose
/// that (NFR-USA-3). Each time says how far it is from UTC, so that it is never taken for
/// another: <c>15 November 2026, 10:20 UTC+1</c>. See docs/times.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A page is handed the times of its request, and asks them for every time that it writes.</b>
/// There is no way to write a time without them, so no page writes one in a time zone of its
/// own. <see cref="Utc"/> is for what has no request and no user: a test, and a part that is
/// written on its own.
/// </para>
/// <para>
/// <b>The distance from UTC is that of the moment itself,</b> and not that of today: a moment
/// in summer says the summer time of its time zone, also when it is read in winter. A moment
/// that is not ahead of UTC nor behind it says <c>UTC</c>, as a page in UTC does.
/// </para>
/// <para>
/// <b>A day that is a UTC day by what it means is not moved:</b> a day of the AI usage, and a
/// day that a provider says of a model. <see cref="UtcDay"/> writes it, for every user alike.
/// </para>
/// </remarks>
public sealed class PageTimes
{
    private readonly TimeZoneInfo _zone;

    private PageTimes(TimeZoneInfo zone) => _zone = zone;

    /// <summary>The times of a page in UTC: what a user who chose UTC reads, and a device that has said no time zone.</summary>
    public static PageTimes Utc { get; } = new(TimeZoneInfo.Utc);

    /// <summary>The times of the page that a request is answered with: see <see cref="PageTimeZone"/>.</summary>
    public static PageTimes Of(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return In(PageTimeZone.Of(request));
    }

    /// <summary>
    /// The times of a page in the time zone that a name names, as <see cref="ZoneId"/> says it:
    /// for a part of a page that is written over a connection, which has no request to ask.
    /// What names no time zone is UTC.
    /// </summary>
    public static PageTimes In(string? zoneId) => DeviceTimeZone.Known(zoneId) is { } zone ? In(zone) : Utc;

    private static PageTimes In(TimeZoneInfo zone) => zone.Equals(TimeZoneInfo.Utc) ? Utc : new PageTimes(zone);

    /// <summary>
    /// The name of the time zone, as the time zone database has it, or null for UTC. It is what
    /// travels with a page to the connection that the page makes: text, which <see cref="In(string?)"/>
    /// reads again.
    /// </summary>
    public string? ZoneId => ReferenceEquals(this, Utc) ? null : _zone.Id;

    /// <summary>A moment as a person reads it, to the minute: <c>15 November 2026, 10:20 UTC+1</c>.</summary>
    public string At(DateTimeOffset time) => Written(time, "d MMMM yyyy, HH:mm") + " " + OffsetAt(time);

    /// <summary>
    /// A moment to the second, for what happens within a minute, as the steps of a cycle do:
    /// <c>15 November 2026, 10:20:07 UTC+1</c>.
    /// </summary>
    public string AtSecond(DateTimeOffset time) => Written(time, "d MMMM yyyy, HH:mm:ss") + " " + OffsetAt(time);

    /// <summary>
    /// The time of day of a moment, to the minute, for what happens today and soon, as the end
    /// of a session does: <c>10:20 UTC+1</c>.
    /// </summary>
    public string TimeOfDay(DateTimeOffset time) => Written(time, "HH:mm") + " " + OffsetAt(time);

    /// <summary>
    /// The day of a moment, as the calendar of the time zone has it: <c>15 November 2026</c>.
    /// A moment late in the evening of one time zone is a moment of the next day in another.
    /// </summary>
    public string Day(DateTimeOffset time) => Written(time, "d MMMM yyyy");

    /// <summary>
    /// How far the time zone is from UTC at a moment, as every time says it: <c>UTC</c>,
    /// <c>UTC+1</c>, <c>UTC-5</c>, <c>UTC+5:30</c>.
    /// </summary>
    public string OffsetAt(DateTimeOffset time)
    {
        var offset = _zone.GetUtcOffset(time);

        // Whole minutes: a time is said to the minute, and so is how far it is from UTC.
        var minutes = (int)Math.Truncate(offset.TotalMinutes);

        if (minutes == 0)
        {
            return "UTC";
        }

        var sign = minutes < 0 ? "-" : "+";
        var hours = (Math.Abs(minutes) / 60).ToString(CultureInfo.InvariantCulture);
        var rest = Math.Abs(minutes) % 60;

        return rest == 0 ? $"UTC{sign}{hours}" : $"UTC{sign}{hours}:{rest.ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// A day that is a UTC day by what it means, for every user alike: <c>15 November 2026</c>.
    /// A day of the AI usage is one, and so is a day that a provider says of a model. It is not
    /// moved into a time zone, which would say the day before to a user west of UTC.
    /// </summary>
    public static string UtcDay(DateTimeOffset day) => day.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private string Written(DateTimeOffset time, string format) =>
        TimeZoneInfo.ConvertTime(time, _zone).ToString(format, CultureInfo.InvariantCulture);
}
