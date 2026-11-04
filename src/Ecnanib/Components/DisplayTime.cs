using System.Globalization;
using Ecnanib.Modules.Identity.Contracts;

namespace Ecnanib.Components;

/// <summary>
/// The time zone a page shows times in (FR-PREF-05). Routes cascades it, so a component takes it as a
/// [CascadingParameter] and writes every time through <see cref="Format"/>. See Preferences in
/// docs/architecture.md.
/// </summary>
public sealed class DisplayTime
{
    /// <summary>
    /// The browser's time zone, which wwwroot/shell.js writes on every page, because the server can't
    /// see it otherwise. Anyone can write it, so it only ever goes through <see cref="TimeZones.TryFind"/>.
    /// </summary>
    public const string BrowserTimeZoneCookie = "ecnanib.time-zone";

    private DisplayTime(TimeZoneInfo zone) => Zone = zone;

    public static DisplayTime Utc { get; } = new(TimeZoneInfo.Utc);

    public TimeZoneInfo Zone { get; }

    /// <summary>The first of <paramref name="ids"/> that names a zone the host knows, or UTC.</summary>
    public static DisplayTime For(params ReadOnlySpan<string?> ids)
    {
        foreach (var id in ids)
        {
            if (TimeZones.TryFind(id, out var zone))
            {
                return zone.Id == TimeZoneInfo.Utc.Id ? Utc : new DisplayTime(zone);
            }
        }

        return Utc;
    }

    public string Format(DateTimeOffset time) =>
        string.Format(CultureInfo.CurrentCulture, Strings.DisplayTime, TimeZoneInfo.ConvertTime(time, Zone));
}
