using Ecnanib.WebUi.Pages;

namespace Ecnanib.WebUi.Layout;

/// <summary>
/// The texts of the banner that announces maintenance on every page, in one place (NFR-USA-4,
/// NFR-REL-6). See "Announced maintenance" in docs/reliability.md.
/// </summary>
public static class MaintenanceTexts
{
    /// <summary>The name of the banner, which a screen reader lists it by among the parts of a page.</summary>
    public static string Label => "Maintenance";

    /// <summary>What the banner says of maintenance that has not started: from when to when, and what it does not do.</summary>
    /// <param name="startsAt">From when.</param>
    /// <param name="endsAt">Until when.</param>
    /// <param name="times">How the page writes a time for whoever reads it.</param>
    public static string Announced(DateTimeOffset startsAt, DateTimeOffset endsAt, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return $"Maintenance is announced from {times.At(startsAt)} to {times.At(endsAt)}. The service may not answer during it. {NoPositionIsClosed}";
    }

    /// <summary>What the banner says of maintenance that has started: until when, and what it does not do.</summary>
    /// <param name="endsAt">Until when.</param>
    /// <param name="times">How the page writes a time for whoever reads it.</param>
    public static string UnderWay(DateTimeOffset endsAt, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return $"Maintenance is under way until {times.At(endsAt)}. The service may not answer for a while. {NoPositionIsClosed}";
    }

    /// <summary>That maintenance closes no position, which ends both sentences of the banner (NFR-REL-6).</summary>
    private static string NoPositionIsClosed =>
        "Open positions keep their protective orders on the exchange, and no position is closed for it.";
}
