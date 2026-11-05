using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Reads the catalog, applying the configuration's metadata over what providers reported (FR-CAT-06).
/// See Model catalog in docs/architecture.md.
/// </summary>
internal sealed class Catalog(IDbContextFactory<EcnanibDbContext> contextFactory, CatalogSettings settings) : ICatalog
{
    public async Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var models = await context.Set<Model>()
            .AsNoTracking()
            .OrderBy(model => model.Provider)
            .ThenBy(model => model.ModelId)
            .ToListAsync(cancellationToken);

        return [.. models.Select(Effective)];
    }

    public async Task<IReadOnlyList<ModelAccess>> ListAccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<ModelAvailability>()
            .AsNoTracking()
            .Where(availability => availability.UserId == userId)
            .OrderBy(availability => availability.CredentialId)
            .ThenBy(availability => availability.ModelId)
            .Select(availability => new ModelAccess(
                availability.CredentialId, availability.Provider, availability.ModelId, availability.Available, availability.CheckedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoleDefault>> ListRoleDefaultsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await context.Set<RoleDefaultRow>().AsNoTracking().ToListAsync(cancellationToken);

        // Stored by name, so ordered here rather than by the database's text order.
        return [.. rows.OrderBy(row => row.Role).Select(row => new RoleDefault(row.Role, row.Provider, row.ModelId))];
    }

    /// <summary>The configuration beats the provider, field by field. Administrators' overrides (#26) will beat both.</summary>
    private CatalogModel Effective(Model model)
    {
        var set = settings.Overrides.GetValueOrDefault((model.Provider, model.ModelId));

        return new CatalogModel(
            model.Provider,
            model.ModelId,
            set?.DisplayName ?? model.DisplayName ?? model.ModelId,
            set?.Family ?? model.Family,
            model.ReleasedAt,
            set?.ContextWindow ?? model.ContextWindow,
            set?.MaxOutputTokens ?? model.MaxOutputTokens,
            new ModelCapabilities(
                set?.ImageInput ?? model.ImageInput,
                set?.StructuredOutput ?? model.StructuredOutput,
                set?.ToolUse ?? model.ToolUse,
                set?.LiveSearch ?? model.LiveSearch,
                set?.ReasoningEffort ?? model.ReasoningEffort),
            model.Status,
            set?.Price,
            model.FirstSeenAt,
            model.LastSeenAt);
    }
}
