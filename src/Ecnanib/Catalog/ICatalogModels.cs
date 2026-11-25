using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// The catalog as a screen lists it: every model of every provider, with what its provider says
/// about it, whether the provider still offers it, and when the provider's catalog was last
/// synced (FR-CAT-10). See docs/catalog.md.
/// </summary>
/// <remarks>
/// It is the same for every user: the rows of the catalog belong to the service, and name no
/// user and no key. Every model is among them, also one that cannot be selected for an agent,
/// and one that its provider no longer offers. What can be selected is for
/// <see cref="ISelectableModels"/> to say.
/// </remarks>
public interface ICatalogModels
{
    /// <summary>
    /// Every AI provider, in the order of the providers' codes, each with its models by
    /// identifier. A provider that the catalog holds no model of is among them.
    /// </summary>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<IReadOnlyList<ProviderCatalog>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The name of every AI provider as users read it, such as "Claude", by the provider's code.
    /// It reads the providers and nothing else: no model, and no sync. So a page that only names
    /// the providers, as the start page does, asks for no more than that.
    /// </summary>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<IReadOnlyDictionary<AiProviderCode, string>> ProviderNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A model of the catalog, whatever its status, or null when the catalog holds no model with
    /// this id. A model is never deleted, so what points at one finds it here for good.
    /// </summary>
    /// <param name="modelId">The id of the model in the catalog.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<CatalogModel?> FindAsync(Guid modelId, CancellationToken cancellationToken);
}

/// <summary>The catalog of one provider's models.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderName">Its name as users read it, such as "Claude".</param>
/// <param name="LastSyncedAt">
/// When the latest sync started that stored the provider's list, or null when none ever did.
/// The models that the list named were last seen then.
/// </param>
/// <param name="LastSyncFailedAt">
/// When the latest sync of the provider started, when that one failed: the catalog is then as
/// the sync of <paramref name="LastSyncedAt"/> left it (FR-CAT-7). Null when the latest sync
/// succeeded, and when there was none. Why it failed is in the row of the sync and in the log,
/// and is not for every user to read.
/// </param>
/// <param name="Models">The models of the provider, by identifier.</param>
public sealed record ProviderCatalog(
    AiProviderCode Provider,
    string ProviderName,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? LastSyncFailedAt,
    IReadOnlyList<CatalogModel> Models);

/// <summary>
/// A model of the catalog, as a screen shows it. What its provider does not say about it is
/// null, which is unknown: nothing is guessed (FR-CAT-4).
/// </summary>
/// <param name="Id">The id of the model in the catalog.</param>
/// <param name="Provider">The provider that offers the model.</param>
/// <param name="ProviderName">The provider's name as users read it.</param>
/// <param name="Identifier">The provider's identifier of the model, which a call to the model names it by.</param>
/// <param name="DisplayName">The name the provider gives the model for people to read.</param>
/// <param name="ContextTokens">The most tokens the model takes as input.</param>
/// <param name="OutputTokens">The most tokens the model writes in one answer.</param>
/// <param name="ReleasedAt">When the provider released the model.</param>
/// <param name="DeprecatedAt">When the provider deprecated the model.</param>
/// <param name="RetiresAt">When the provider plans to stop serving the model.</param>
/// <param name="Status">Whether the provider still offers the model (FR-CAT-5).</param>
/// <param name="LastSeenAt">When the sync started that last found the model in the provider's list.</param>
/// <param name="Selectable">
/// Whether the model can be selected for an agent: its provider still offers it, and the
/// configuration file allows it (FR-CAT-6). It is what <see cref="ISelectableModels"/> goes by.
/// </param>
/// <param name="LiveSearch">
/// Whether the configuration file says that the model can search live (FR-CYC-3). It says so of
/// the model, and allows nothing: a model that cannot be selected may search live all the same.
/// </param>
public sealed record CatalogModel(
    Guid Id,
    AiProviderCode Provider,
    string ProviderName,
    string Identifier,
    string? DisplayName,
    int? ContextTokens,
    int? OutputTokens,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? DeprecatedAt,
    DateTimeOffset? RetiresAt,
    AiModelStatus Status,
    DateTimeOffset LastSeenAt,
    bool Selectable,
    bool LiveSearch);
