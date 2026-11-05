using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Reads the catalog, applying the configuration's metadata over what providers reported (FR-CAT-06), and
/// administrators' overrides over both (FR-CAT-05). See Model catalog in docs/architecture.md.
/// </summary>
internal sealed class Catalog(IDbContextFactory<EcnanibDbContext> contextFactory, CatalogSettings settings, IAiProviders providers) : ICatalog
{
    public IReadOnlyList<string> Providers { get; } = [.. providers.All.Select(provider => provider.Name).Order(StringComparer.Ordinal)];

    public async Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var models = await context.Set<Model>()
            .AsNoTracking()
            .OrderBy(model => model.Provider)
            .ThenBy(model => model.ModelId)
            .ToListAsync(cancellationToken);
        var overrides = await context.Set<ModelOverrideRow>()
            .AsNoTracking()
            .ToDictionaryAsync(row => (row.Provider, row.ModelId), cancellationToken);

        return [.. models.Select(model => Effective(model, overrides.GetValueOrDefault((model.Provider, model.ModelId))))];
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

        var rows = await context.Set<RoleDefaultRow>().AsNoTracking().Where(row => row.Provider != null).ToListAsync(cancellationToken);

        // Stored by name, so ordered here rather than by the database's text order.
        return [.. rows.OrderBy(row => row.Role).Select(row => new RoleDefault(row.Role, row.Provider!, row.ModelId))];
    }

    /// <summary>What the provider reported, for the fields an administrator can override.</summary>
    public static ModelLayer Reported(Model model) => new(
        model.DisplayName,
        new ModelCapabilities(model.ImageInput, model.StructuredOutput, model.ToolUse, model.LiveSearch, model.ReasoningEffort),
        null);

    /// <summary>The configuration's entry for a model, for the fields an administrator can override, or null.</summary>
    public ModelLayer? Configured(string provider, string modelId) =>
        settings.Overrides.GetValueOrDefault((provider, modelId)) is { } set
            ? new ModelLayer(set.DisplayName, new ModelCapabilities(set.ImageInput, set.StructuredOutput, set.ToolUse, set.LiveSearch, set.ReasoningEffort), set.Price)
            : null;

    /// <summary>
    /// Administrators beat the configuration, which beats the provider, field by field. Hiding doesn't touch the
    /// status, which stays the model's lifecycle.
    /// </summary>
    public CatalogModel Effective(Model model, ModelOverrideRow? row)
    {
        var set = settings.Overrides.GetValueOrDefault((model.Provider, model.ModelId));
        var edit = row?.ToEdit() ?? ModelEdit.None;
        var reported = Reported(model).Capabilities;
        var configured = Configured(model.Provider, model.ModelId)?.Capabilities ?? ModelCapabilities.Unknown;

        return new CatalogModel(
            model.Provider,
            model.ModelId,
            edit.DisplayName ?? set?.DisplayName ?? model.DisplayName ?? model.ModelId,
            set?.Family ?? model.Family,
            model.ReleasedAt,
            set?.ContextWindow ?? model.ContextWindow,
            set?.MaxOutputTokens ?? model.MaxOutputTokens,
            reported.Under(configured).Under(edit.Capabilities),
            model.Status,
            edit.Price ?? set?.Price,
            model.FirstSeenAt,
            model.LastSeenAt,
            edit.Hidden,
            [.. edit.RecommendedRoles]);
    }
}
