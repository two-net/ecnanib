using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Catalog;

/// <summary>
/// Says which models of the catalog can be selected: the active ones that the configuration
/// file allows (FR-CAT-6). See <see cref="ModelEligibility"/> for what the file says.
/// </summary>
/// <remarks>
/// The active models are read, and the patterns of the file are then held against each of them
/// here, and not in the database. A model that is unavailable is never among them, and nothing
/// is put in its place: what selected it finds it gone (FR-CAT-8).
/// </remarks>
internal sealed class SelectableModels(ModelEligibility eligibility, IDbContextFactory<EcnanibDbContext> contexts)
    : ISelectableModels
{
    public async Task<IReadOnlyList<SelectableModel>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var active = await context.Set<AiModel>()
            .AsNoTracking()
            .Where(model => model.Status == AiModelStatus.Active)
            .Select(model => new { model.Id, model.Provider, model.Identifier, model.DisplayName })
            .ToListAsync(cancellationToken);

        // In an order that is the same wherever the database runs.
        return
        [
            .. active
                .Where(model => eligibility.IsEligible(model.Provider, model.Identifier))
                .OrderBy(model => model.Provider.Value, StringComparer.Ordinal)
                .ThenBy(model => model.Identifier, StringComparer.Ordinal)
                .Select(model => new SelectableModel(
                    model.Id,
                    model.Provider,
                    model.Identifier,
                    model.DisplayName,
                    eligibility.SearchesLive(model.Provider, model.Identifier))),
        ];
    }

    public async Task<SelectableModel?> FindAsync(Guid modelId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var model = await context.Set<AiModel>()
            .AsNoTracking()
            .Where(stored => stored.Id == modelId && stored.Status == AiModelStatus.Active)
            .Select(stored => new { stored.Id, stored.Provider, stored.Identifier, stored.DisplayName })
            .SingleOrDefaultAsync(cancellationToken);

        if (model is null || !eligibility.IsEligible(model.Provider, model.Identifier))
        {
            return null;
        }

        return new SelectableModel(
            model.Id,
            model.Provider,
            model.Identifier,
            model.DisplayName,
            eligibility.SearchesLive(model.Provider, model.Identifier));
    }
}
