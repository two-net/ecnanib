using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Catalog;

/// <summary>
/// Syncs the catalog on a schedule: every <c>CatalogSync:IntervalHours</c> of the configuration
/// file, and as the application starts when the catalog is empty (FR-CAT-2). See docs/catalog.md.
/// </summary>
/// <remarks>
/// <para>
/// A run syncs every provider that the configuration file holds a catalog key for, with that
/// key (FR-CAT-3). A provider without one is not the job's: its catalog is refreshed when a
/// user's key for it is validated. With no catalog key at all, a run asks nobody and nothing.
/// </para>
/// <para>
/// A provider whose sync fails does not fail the run: the failure is recorded and logged, and
/// the provider is asked again with the next run. A run can end anywhere and be repeated: each
/// provider is stored on its own, in one transaction.
/// </para>
/// </remarks>
internal sealed class CatalogSyncJob(CatalogSync sync, CatalogSyncSettings settings) : IBackgroundJob, IDueAtStart
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "catalog-sync";

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(TimeSpan.FromHours(settings.IntervalHours));

    public Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken) =>
        sync.SyncAsync(CatalogSyncCause.Scheduled, cancellationToken);

    /// <summary>
    /// A catalog that is empty is synced as the application starts, and not when the job is next
    /// due: a provider that the service has a key for, and no model of, is one that the first
    /// sync did not reach, or whose key the file has only now.
    /// </summary>
    public Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => sync.IsEmptyAsync(cancellationToken);
}
