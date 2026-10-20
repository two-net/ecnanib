using Ecnanib.Ai.Abstractions;

namespace Ecnanib.ModelSync.Abstractions;

/// <summary>
/// The on-demand catalog sync (FR-SYNC-02): lists the provider's models with a valid stored key, the requester's own
/// first, and writes the list through <see cref="IProviderCatalogSync"/> with trigger Manual, which counts misses and
/// deprecates (FR-SYNC-03). A missing key, a refused key or a provider failure is an outcome; an exception means the
/// database failed. Syncs of one provider run one at a time, whoever starts them.
/// </summary>
public interface IModelSyncService
{
    Task<ModelSyncOutcome> SyncNowAsync(AiProvider provider, Guid requestedBy, CancellationToken cancellationToken);
}

public enum ModelSyncStatus
{
    /// <summary>The list was written; <see cref="ModelSyncOutcome.Result"/> carries the counts.</summary>
    Synced = 1,

    /// <summary>No stored key of the provider is Valid: nothing was called and nothing was recorded.</summary>
    NoValidKey = 2,

    /// <summary>The provider failed, listed nothing or rejected every key tried; recorded as the provider's last result (FR-SYNC-06).</summary>
    Failed = 3,
}

/// <summary><see cref="Detail"/> explains <see cref="ModelSyncStatus.NoValidKey"/> and <see cref="ModelSyncStatus.Failed"/>, fit to show.</summary>
public sealed record ModelSyncOutcome(ModelSyncStatus Status, AiProvider Provider, CatalogSyncResult? Result = null, string? Detail = null);
