using Ecnanib.AiProviderGateway;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Catalog;

/// <summary>
/// Reads the catalog for the screen that lists it (FR-CAT-10): the models of each provider as
/// they are stored, and when the provider's catalog was last synced. See
/// <see cref="ModelEligibility"/> for what the configuration file says of a model.
/// </summary>
/// <remarks>
/// Nothing is written, and no provider is asked. What a provider did not say about a model is
/// handed on as it is stored, which is as nothing: how unknown is worded is the screen's.
/// </remarks>
internal sealed class CatalogModels(ModelEligibility eligibility, IDbContextFactory<EcnanibDbContext> contexts)
    : ICatalogModels
{
    public async Task<IReadOnlyList<ProviderCatalog>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The providers are the rows of their table, and not a list of this code: a provider
        // that is added there has a place here, also before a model of it is known.
        var providers = await context.Set<AiProvider>()
            .AsNoTracking()
            .OrderBy(provider => provider.Code)
            .ToListAsync(cancellationToken);

        var models = (await context.Set<AiModel>().AsNoTracking().ToListAsync(cancellationToken))
            .ToLookup(model => model.Provider);

        var catalogs = new List<ProviderCatalog>(providers.Count);

        foreach (var provider in providers)
        {
            var (syncedAt, failedAt) = await LastSyncsAsync(context, provider.Code, cancellationToken);

            catalogs.Add(new ProviderCatalog(
                provider.Code,
                provider.DisplayName,
                syncedAt,
                failedAt,
                [
                    // In an order that is the same wherever the database runs.
                    .. models[provider.Code]
                        .OrderBy(model => model.Identifier, StringComparer.Ordinal)
                        .Select(model => Shown(model, provider.DisplayName)),
                ]));
        }

        return catalogs;
    }

    public async Task<IReadOnlyDictionary<AiProviderCode, string>> ProviderNamesAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The rows of the providers, which are few and seeded: nothing of their models is read.
        return await context.Set<AiProvider>()
            .AsNoTracking()
            .ToDictionaryAsync(provider => provider.Code, provider => provider.DisplayName, cancellationToken);
    }

    public async Task<CatalogModel?> FindAsync(Guid modelId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var model = await context.Set<AiModel>()
            .AsNoTracking()
            .SingleOrDefaultAsync(stored => stored.Id == modelId, cancellationToken);

        if (model is null)
        {
            return null;
        }

        // A model points at its provider, which is a row that is never deleted while it does.
        var providerName = await context.Set<AiProvider>()
            .Where(provider => provider.Code == model.Provider)
            .Select(provider => provider.DisplayName)
            .SingleAsync(cancellationToken);

        return Shown(model, providerName);
    }

    /// <summary>
    /// When the provider's catalog was last synced, and when its latest sync started if that one
    /// failed. Each is the newest row of the provider's syncs, which their index finds: the
    /// table has a row for every sync there ever was, and none of the others is read.
    /// </summary>
    private static async Task<(DateTimeOffset? SyncedAt, DateTimeOffset? FailedAt)> LastSyncsAsync(
        EcnanibDbContext context, AiProviderCode provider, CancellationToken cancellationToken)
    {
        var syncs = context.Set<CatalogSyncRun>().AsNoTracking().Where(sync => sync.Provider == provider);

        var latest = await syncs
            .OrderByDescending(sync => sync.StartedAt)
            .ThenByDescending(sync => sync.Id)
            .Select(sync => new { sync.StartedAt, sync.Result })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return (null, null);
        }

        if (latest.Result == CatalogSyncResult.Succeeded)
        {
            return (latest.StartedAt, null);
        }

        // The latest one failed, so the catalog is as the last one that succeeded left it.
        var synced = await syncs
            .Where(sync => sync.Result == CatalogSyncResult.Succeeded)
            .OrderByDescending(sync => sync.StartedAt)
            .ThenByDescending(sync => sync.Id)
            .Select(sync => (DateTimeOffset?)sync.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return (synced, latest.StartedAt);
    }

    private CatalogModel Shown(AiModel model, string providerName) =>
        new(
            model.Id,
            model.Provider,
            providerName,
            model.Identifier,
            model.DisplayName,
            model.ContextTokens,
            model.OutputTokens,
            model.ReleasedAt,
            model.DeprecatedAt,
            model.RetiresAt,
            model.Status,
            model.LastSeenAt,
            Selectable: model.Status == AiModelStatus.Active && eligibility.IsEligible(model.Provider, model.Identifier),
            LiveSearch: eligibility.SearchesLive(model.Provider, model.Identifier));
}
