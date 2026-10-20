using Ecnanib.Ai.Abstractions;
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
            .Select(m => new CatalogModel(m.Provider, m.ModelId, m.DisplayName, m.Status, m.FirstSeenAt, m.LastSeenAt, m.ProviderCreatedAt, m.InputTokenLimit, m.OutputTokenLimit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderSyncView>> GetSyncStatesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var states = await db.ProviderSyncStates.AsNoTracking().ToDictionaryAsync(s => s.Provider, cancellationToken).ConfigureAwait(false);
        var counts = await db.ProviderModels.AsNoTracking()
            .GroupBy(m => new { m.Provider, m.Status })
            .Select(g => new { g.Key.Provider, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Enum.GetValues<AiProvider>().Select(provider =>
        {
            states.TryGetValue(provider, out var state);
            return new ProviderSyncView(
                provider,
                state?.LastSyncAt,
                state?.LastStatus ?? SyncStatus.Never,
                state?.LastError,
                state?.ModelCount ?? 0,
                state?.Trigger,
                counts.Where(c => c.Provider == provider && c.Status == ModelStatus.Available).Sum(c => c.Count),
                counts.Where(c => c.Provider == provider && c.Status == ModelStatus.Deprecated).Sum(c => c.Count));
        }).ToList();
    }
}
