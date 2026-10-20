using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.ModelSync.Abstractions;

/// <summary>One catalog entry as the UI lists it (FR-SYNC-01, FR-AI-03).</summary>
public sealed record CatalogModel(AiProvider Provider, string ModelId, string DisplayName, ModelStatus Status);

/// <summary>
/// The synced model catalog for choosing council models (FR-AI-03, FR-SYNC-05): every provider's models, available
/// ones first and the most recently added first within a provider (the same order that picks a provider's default).
/// The catalog is shared by all users (FR-SYNC-02).
/// </summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken);
}
