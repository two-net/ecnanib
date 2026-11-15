using Ecnanib.Catalog;
using Ecnanib.Shared;

namespace Ecnanib.Credentials;

/// <summary>
/// Which models a user can select for an agent: the models of the catalog that can be selected
/// at all, and that the user's own key for their provider can use (FR-CAT-6, FR-CAT-9). It is
/// what a model selector offers, and what holds a choice to it when the choice is saved. See
/// docs/credentials.md.
/// </summary>
/// <remarks>
/// <para>
/// It is answered from what was recorded when the provider last took the user's key and listed
/// its models to it, and asks no provider. A key that its provider has given no list yet offers
/// nothing: see <see cref="ModelBlock.NotKnown"/>.
/// </para>
/// <para>
/// A method takes the user by id, and not from a request, as <see cref="IKeyValidation"/> does.
/// What calls it takes the id from the request that is signed in, or from the row it works on,
/// and never from what came in from outside. See docs/ownership.md.
/// </para>
/// </remarks>
public interface IKeyModels
{
    /// <summary>
    /// What a user can select, for every AI provider, in the order of the providers' codes. A
    /// provider that offers the user nothing is among them, with why.
    /// </summary>
    /// <param name="userId">The user who selects.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<IReadOnlyList<ProviderModels>> SelectableAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// What keeps a user from selecting a model, or null when nothing does. It is asked again
    /// when a choice is saved: what a selector offered may have changed since, and what is
    /// posted need not have come from one.
    /// </summary>
    /// <param name="userId">The user who selects.</param>
    /// <param name="modelId">The id of the model in the catalog.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<ModelBlock?> BlockForAsync(Guid userId, Guid modelId, CancellationToken cancellationToken);
}

/// <summary>What a user can select of one AI provider's models.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Block">
/// Why the user can select none of the provider's models, as far as their key goes, or null when
/// the key can be used: <see cref="ModelBlock.NoKey"/>, <see cref="ModelBlock.KeyNotValid"/>, or
/// <see cref="ModelBlock.NotKnown"/>.
/// </param>
/// <param name="Models">
/// The models the user can select, by identifier. It is empty with a <paramref name="Block"/>,
/// and also when the key can use none of the models that can be selected.
/// </param>
public sealed record ProviderModels(AiProviderCode Provider, ModelBlock? Block, IReadOnlyList<SelectableModel> Models);

/// <summary>What keeps a user from selecting a model for an agent.</summary>
public enum ModelBlock
{
    /// <summary>
    /// The model cannot be selected by anyone: the catalog holds no such model, its provider no
    /// longer offers it, or the configuration file does not allow it (FR-CAT-6).
    /// </summary>
    NotSelectable,

    /// <summary>The user has stored no key for the provider of the model.</summary>
    NoKey,

    /// <summary>The user's key for the provider is not valid: the provider refused it when it was last asked, or it was never checked.</summary>
    KeyNotValid,

    /// <summary>
    /// It is not known which models the user's key can use: the provider took the key, and has
    /// given no list of its models to it yet. The next check of the key asks again, which is the
    /// daily one, or the one the user asks for on the page of their keys.
    /// </summary>
    NotKnown,

    /// <summary>The provider did not list the model to the user's key, so the key cannot use it (FR-CAT-9).</summary>
    NotListed,
}
