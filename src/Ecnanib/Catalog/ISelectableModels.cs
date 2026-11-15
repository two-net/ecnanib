using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// The models of the catalog that can be selected for an agent (FR-CAT-6): those that their
/// provider still offers, and that the configuration file allows. See docs/catalog.md.
/// </summary>
/// <remarks>
/// It says what can be selected at all, which is the same for every user. Which of these a
/// user's own key can use is for Credentials to say, with <c>IKeyModels</c>, and that is what a
/// selector offers (FR-CAT-9).
/// </remarks>
public interface ISelectableModels
{
    /// <summary>Every model that can be selected, by provider and then by identifier.</summary>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<IReadOnlyList<SelectableModel>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A model, when it can be selected, or null: when the catalog holds no such model, when its
    /// provider no longer offers it, and when the configuration file does not allow it.
    /// </summary>
    /// <param name="modelId">The id of the model in the catalog.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<SelectableModel?> FindAsync(Guid modelId, CancellationToken cancellationToken);
}

/// <summary>A model that can be selected for an agent.</summary>
/// <param name="Id">The id of the model in the catalog, which is what an agent keeps of its choice.</param>
/// <param name="Provider">The provider that offers the model.</param>
/// <param name="Identifier">The provider's identifier of the model, which a call to the model names it by.</param>
/// <param name="DisplayName">The name the provider gives the model for people to read, or null when it gives none.</param>
/// <param name="LiveSearch">
/// Whether the configuration file says that the model can search live, which a selector shows
/// (FR-CYC-3). A model that cannot is selected like any other.
/// </param>
public sealed record SelectableModel(Guid Id, AiProviderCode Provider, string Identifier, string? DisplayName, bool LiveSearch);
