namespace Ecnanib.Platform.Settings;

/// <summary>The <c>CatalogSync</c> section: how often the model catalog is synced (FR-CAT-2).</summary>
/// <param name="IntervalHours">The time between scheduled syncs.</param>
/// <param name="MissedSyncsBeforeUnavailable">
/// How many syncs in a row a model may be missing from before it is marked unavailable.
/// </param>
public sealed record CatalogSyncSettings(int IntervalHours, int MissedSyncsBeforeUnavailable)
{
    internal static CatalogSyncSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("CatalogSync:IntervalHours"),
            file.WholeNumber("CatalogSync:MissedSyncsBeforeUnavailable"));
}
