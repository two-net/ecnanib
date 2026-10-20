using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.ModelSync.Services;

internal sealed class ModelCatalog : IModelCatalog
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public ModelCatalog(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ProviderModels.AsNoTracking()
            .OrderBy(m => m.Provider)
            .ThenBy(m => m.Status)
            .ThenByDescending(m => m.ProviderCreatedAt ?? m.FirstSeenAt)
            .ThenBy(m => m.ModelId)
            .Select(m => new CatalogModel(m.Provider, m.ModelId, m.DisplayName, m.Status))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
