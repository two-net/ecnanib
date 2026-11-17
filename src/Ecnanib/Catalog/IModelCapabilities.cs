using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// What the configuration file says that a model can do, beyond being selected (FR-CAT-6). The
/// providers do not report what their models can do in one way, so the file says it, as a
/// capability flag for each provider. See docs/catalog.md.
/// </summary>
/// <remarks>
/// <para>
/// It answers from the file alone, which is read once as the application starts: it asks no
/// database and no provider, and says the same of a model for as long as the application runs.
/// </para>
/// <para>
/// It is what a selector shows of a model, through <see cref="ISelectableModels"/>. So what a
/// user was shown when they chose a model, and what a call of that model goes by, are the same.
/// </para>
/// </remarks>
public interface IModelCapabilities
{
    /// <summary>
    /// Whether the file says of a model that it can search live: a pattern of its provider's
    /// <c>LiveSearch</c> list names it (FR-CYC-3). It says so of a model whatever its status, and
    /// whether or not the model can be selected.
    /// </summary>
    /// <param name="provider">The provider that offers the model.</param>
    /// <param name="identifier">The provider's identifier of the model.</param>
    bool SearchesLive(AiProviderCode provider, string identifier);
}
