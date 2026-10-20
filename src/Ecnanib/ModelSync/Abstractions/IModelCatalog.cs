using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.ModelSync.Abstractions;

/// <summary>One catalog entry as the UI lists it (FR-SYNC-01, FR-AI-03).</summary>
public sealed record CatalogModel(
    AiProvider Provider,
    string ModelId,
    string DisplayName,
    ModelStatus Status,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ProviderCreatedAt,
    int? InputTokenLimit,
    int? OutputTokenLimit);

/// <summary>
/// The last sync of one provider (FR-SYNC-06, NFR-OBS-01) with the catalog's counts. A provider never synced answers
/// <see cref="SyncStatus.Never"/>, a null time and zero counts.
/// </summary>
public sealed record ProviderSyncView(
    AiProvider Provider,
    DateTimeOffset? LastSyncAt,
    SyncStatus LastStatus,
    string? LastError,
    int ModelCount,
    SyncTrigger? Trigger,
    int Available,
    int Deprecated);

/// <summary>
/// The synced model catalog for choosing council models (FR-AI-03, FR-SYNC-05): every provider's models, available
/// ones first and the most recently added first within a provider (the same order that picks a provider's default),
/// and the last sync per provider for the providers page and the health status. The catalog is shared by all users
/// (FR-SYNC-02).
/// </summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One view per <see cref="AiProvider"/>, in enum order, whether or not it was ever synced.</summary>
    Task<IReadOnlyList<ProviderSyncView>> GetSyncStatesAsync(CancellationToken cancellationToken);
}
