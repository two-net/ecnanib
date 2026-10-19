using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.ModelSync.Abstractions;

/// <summary>
/// Writes a provider's model list into the shared catalog (FR-SYNC-01) and records the sync (FR-SYNC-06). This is the
/// seam the keys module calls after a user adds or validates a provider key (FR-SYNC-02). Fetching on a schedule or on
/// demand, miss counting and deprecation (FR-SYNC-03) arrive with the model-sync issue.
/// </summary>
public interface IProviderCatalogSync
{
    /// <summary>Upserts <paramref name="models"/> (all of <paramref name="provider"/>) and records a successful sync; a failure is recorded and rethrown.</summary>
    Task<CatalogSyncResult> SyncAsync(AiProvider provider, IReadOnlyList<AiModelInfo> models, SyncTrigger trigger, CancellationToken cancellationToken);
}

public sealed record CatalogSyncResult(AiProvider Provider, int Added, int Updated, int Listed, DateTimeOffset SyncedAt);
