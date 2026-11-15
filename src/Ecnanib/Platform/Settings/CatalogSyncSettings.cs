namespace Ecnanib.Platform.Settings;

/// <summary>The <c>CatalogSync</c> section: how often the model catalog is synced (FR-CAT-2).</summary>
/// <param name="IntervalHours">The time between scheduled syncs: from an hour to a year.</param>
/// <param name="MissedSyncsBeforeUnavailable">
/// How many syncs in a row a model may be missing from before it is marked unavailable: from 1 to 100.
/// </param>
public sealed record CatalogSyncSettings(int IntervalHours, int MissedSyncsBeforeUnavailable)
{
    internal static CatalogSyncSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("CatalogSync:IntervalHours", minimum: 1, maximum: MostIntervalHours),
            file.WholeNumber("CatalogSync:MissedSyncsBeforeUnavailable", minimum: 1, maximum: MostMissedSyncs));

    // A year. The times that are worked out from these two are spans of time on a calendar, so
    // neither may be so large that one of them is no time at all.
    private const int MostIntervalHours = 8760;
    private const int MostMissedSyncs = 100;
}
