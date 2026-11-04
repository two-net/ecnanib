using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Syncs every user's valid AI keys on Catalog:SyncIntervalHours (FR-CAT-03), and recovers whatever a lost
/// credential event missed.
/// </summary>
internal sealed class CatalogSyncJob(CatalogSync sync, CatalogSettings settings) : IRecurringJob
{
    /// <summary>The schedule reads its last start under this name, so it never changes.</summary>
    public const string JobName = "catalog-sync";

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(settings.SyncInterval);

    public TimeSpan Timeout => CatalogSync.FullTimeout;

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        sync.SyncEveryoneAsync(CatalogSyncTrigger.Schedule, context, cancellationToken);
}
