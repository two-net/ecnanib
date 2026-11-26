using Ecnanib.Accounts;

namespace Ecnanib.WebUi.TimeZones;

/// <summary>
/// The time zone that a page writes its times in: times are shown in the user's local time
/// zone, with UTC available (NFR-USA-3). The server decides it for every page that it answers
/// with, and writes each time in it, so no script has a part in what a time says. See
/// docs/times.md.
/// </summary>
internal static class PageTimeZone
{
    /// <summary>
    /// The time zone of the page that a request is answered with. For a user whose account says
    /// UTC it is UTC, on every device. For every other request it is the time zone that the
    /// device has said, and UTC for a device that has said none.
    /// </summary>
    public static TimeZoneInfo Of(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SignedInUser.TimeDisplayOf(request.User) == TimeDisplay.Utc
            ? TimeZoneInfo.Utc
            : DeviceTimeZone.Of(request.Request) ?? TimeZoneInfo.Utc;
    }

    /// <summary>
    /// What the account of a request says about the time zone of its pages: the one of the
    /// device, or UTC. A request that is not signed in has chosen nothing, which is the device.
    /// </summary>
    public static TimeDisplay DisplayOf(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SignedInUser.TimeDisplayOf(request.User) ?? TimeDisplay.Device;
    }

    /// <summary>A choice as a form spells it: <c>device</c> or <c>utc</c>.</summary>
    public static string WordOf(TimeDisplay display) => display switch
    {
        TimeDisplay.Utc => "utc",
        _ => "device",
    };

    /// <summary>The choice that a word names, or null for anything that is no such word.</summary>
    public static TimeDisplay? OfWord(string? word) => word switch
    {
        "device" => TimeDisplay.Device,
        "utc" => TimeDisplay.Utc,
        _ => null,
    };
}
