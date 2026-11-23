using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// What the tokens of a model cost, as the configuration file says for its provider (FR-CAT-11).
/// The providers do not report their prices in one way, so the file says them, for the models
/// that a deployment gives a price. See "Prices" in docs/catalog.md.
/// </summary>
/// <remarks>
/// <para>
/// It answers from the file alone, which is read once as the application starts: it asks no
/// database and no provider, and says the same of a model for as long as the application runs.
/// So a price that was changed holds from the next start, also for the calls that were made
/// before: what a call cost is estimated with the price that the file names now.
/// </para>
/// <para>
/// A model that the file gives no price has none. What its tokens cost is then not known, which
/// is not the same as nothing: whatever estimates a cost says so, and adds up no zero for it.
/// </para>
/// </remarks>
public interface IModelPrices
{
    /// <summary>
    /// The price of a model: the first entry of its provider's <c>Prices</c> list whose pattern
    /// names it, in the order of the file. Null when no entry names it, and when the file has
    /// no entry for its provider. It says so of a model whatever its status, and whether or not
    /// the model can be selected.
    /// </summary>
    /// <param name="provider">The provider that offers the model.</param>
    /// <param name="identifier">The provider's identifier of the model.</param>
    ModelPrice? Of(AiProviderCode provider, string identifier);
}

/// <summary>What the tokens of a model cost, in US dollars for a million tokens, as the providers publish their prices.</summary>
/// <param name="InputPerMillionTokens">What a million tokens of a prompt cost.</param>
/// <param name="OutputPerMillionTokens">What a million tokens of an answer cost.</param>
public sealed record ModelPrice(decimal InputPerMillionTokens, decimal OutputPerMillionTokens)
{
    private const decimal Million = 1_000_000m;

    /// <summary>
    /// What a number of tokens cost at this price, in US dollars. It is an estimate: a provider
    /// may charge some tokens at another price than the one it publishes, such as those that it
    /// read from a cache, and charges for more than tokens.
    /// </summary>
    /// <param name="inputTokens">The tokens of the prompts.</param>
    /// <param name="outputTokens">The tokens of the answers.</param>
    public decimal CostOf(long inputTokens, long outputTokens) =>
        ((inputTokens * InputPerMillionTokens) + (outputTokens * OutputPerMillionTokens)) / Million;
}

/// <summary>Answers <see cref="IModelPrices"/> from the <c>AiProviders</c> section of the configuration file.</summary>
internal sealed class ModelPrices(AiProvidersSettings providers) : IModelPrices
{
    public ModelPrice? Of(AiProviderCode provider, string identifier)
    {
        if (!providers.ByCode.TryGetValue(provider, out var entry))
        {
            return null;
        }

        // In the order of the file: the first entry that names the model is its price.
        foreach (var price in entry.Prices)
        {
            if (ModelPattern.Names(price.Model, identifier))
            {
                return new ModelPrice(price.InputPerMillionTokens, price.OutputPerMillionTokens);
            }
        }

        return null;
    }
}
