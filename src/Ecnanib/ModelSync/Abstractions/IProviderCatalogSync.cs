using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.ModelSync.Abstractions;

/// <summary>
/// Writes a provider's model list into the shared catalog (FR-SYNC-01) and records the sync (FR-SYNC-06). The keys
/// module calls it after a user adds or validates a provider key (FR-SYNC-02); the module's own scheduled and
/// on-demand syncs call it with the list they fetched (<see cref="IModelSyncService"/>).
/// </summary>
public interface IProviderCatalogSync
{
    /// <summary>
    /// Upserts <paramref name="models"/> (all of <paramref name="provider"/>) and records a successful sync; a failure is
    /// recorded and rethrown. With trigger Scheduled or Manual every catalog row absent from the list counts one miss and
    /// is deprecated once <c>Providers:DeprecationMissThreshold</c> is reached (FR-SYNC-03); KeyValidation leaves absent
    /// rows untouched, so a user's validation never deprecates.
    /// </summary>
    Task<CatalogSyncResult> SyncAsync(AiProvider provider, IReadOnlyList<AiModelInfo> models, SyncTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Records a sync that produced no list (provider failure, every key rejected) as the provider's last result
    /// (FR-SYNC-06). The catalog is not touched and no miss is counted. Best effort: a failure to record is logged at
    /// Warning and swallowed, so the caller's own error stays the one that surfaces.
    /// </summary>
    Task RecordFailureAsync(AiProvider provider, SyncTrigger trigger, string error, CancellationToken cancellationToken);
}

/// <summary><see cref="Missed"/> rows were absent from the list and counted; <see cref="Deprecated"/> of them crossed the threshold in this sync.</summary>
public sealed record CatalogSyncResult(AiProvider Provider, int Added, int Updated, int Listed, DateTimeOffset SyncedAt, int Missed = 0, int Deprecated = 0);
