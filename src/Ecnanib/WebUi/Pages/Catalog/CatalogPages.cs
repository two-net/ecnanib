using System.Globalization;
using Ecnanib.Catalog;

namespace Ecnanib.WebUi.Pages.Catalog;

/// <summary>
/// The texts of the pages about the catalog of models, in one place (NFR-USA-4): the page of the
/// catalog, how a model is named, how what its provider says about it is written, and what is
/// written for what the provider does not say, which is that it is unknown (FR-CAT-4,
/// FR-CAT-10). The page is named by its screen: see <c>Screens.Catalog</c>. A table of any page
/// names its column of providers and its column of models with the headings here.
/// </summary>
public static class CatalogPages
{
    /// <summary>What the page of the catalog says first: whose lists it shows, and that nothing is added to them (FR-CAT-4).</summary>
    public static string Explained =>
        "These are the models that each AI provider lists as its own. Ecnanib reads the lists from the providers and adds " +
        "nothing to them: what a provider does not say about a model is shown as unknown.";

    /// <summary>
    /// When a model can be given to an agent, by the heading of the column that says so, and
    /// what becomes of a model that its provider no longer lists (FR-CAT-5).
    /// </summary>
    public static string SelectableExplained =>
        $"A model can be given to an agent when \"{ForAgentsHeading}\" says yes, and your own key for its provider can use it. " +
        "A model that its provider no longer lists stays here as unavailable.";

    /// <summary>The heading of the name that a provider gives a model, in the table of the catalog.</summary>
    public static string NameHeading => "Name";

    /// <summary>The heading of the identifier that a provider names a model by.</summary>
    public static string IdentifierHeading => "Identifier";

    /// <summary>The heading of whether the provider still offers a model.</summary>
    public static string StatusHeading => "Status";

    /// <summary>The heading of whether a model can be given to an agent.</summary>
    public static string ForAgentsHeading => "For agents";

    /// <summary>The heading of whether a model can search live.</summary>
    public static string LiveSearchHeading => "Live search";

    /// <summary>The heading of how many tokens a model takes in.</summary>
    public static string ContextHeading => "Context";

    /// <summary>The heading of how many tokens a model gives out.</summary>
    public static string OutputHeading => "Output";

    /// <summary>The heading of the day that a model was released.</summary>
    public static string ReleasedHeading => "Released";

    /// <summary>The heading of the day that a model was deprecated.</summary>
    public static string DeprecatedHeading => "Deprecated";

    /// <summary>The heading of the day that a model retires.</summary>
    public static string RetiresHeading => "Retires";

    /// <summary>The heading of the links to the experience records of the models.</summary>
    public static string ExperienceHeading => "Experience";

    /// <summary>
    /// The heading of a column that names a provider, in a table of any page: the experience
    /// screens, the board, and the page of the AI usage have such tables.
    /// </summary>
    public static string ProviderHeading => "Provider";

    /// <summary>The heading of a column that names a model as <see cref="NameOf"/> does, in a table of any page.</summary>
    public static string ModelHeading => "Model";

    /// <summary>
    /// What is written in place of something that a provider does not say about a model.
    /// Nothing is guessed, and nothing is left blank: a blank would read as "none".
    /// </summary>
    public static string Unknown => "Unknown";

    /// <summary>What the page says of a provider that it lists no model of.</summary>
    public static string NoModels(string providerName) => $"No model of {providerName} is listed yet.";

    /// <summary>The name of the table of the models of a provider.</summary>
    public static string ModelsCaption(string providerName) => $"The models of {providerName}";

    /// <summary>What a model is called on a page: by the name its provider gives it, or by its identifier when the provider gives none.</summary>
    public static string NameOf(CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.DisplayName ?? model.Identifier;
    }

    /// <summary>A number of tokens, with its thousands apart, or that it is unknown.</summary>
    public static string Tokens(int? tokens) =>
        tokens is { } count ? $"{count.ToString("N0", CultureInfo.InvariantCulture)} tokens" : Unknown;

    /// <summary>The day of something that a provider says about a model, or that it is unknown. The day is UTC.</summary>
    public static string Day(DateTimeOffset? time) => time is { } known ? PageTimes.UtcDay(known) : Unknown;

    /// <summary>Whether something holds of a model, in a word.</summary>
    public static string YesOrNo(bool holds) => holds ? "Yes" : "No";

    /// <summary>
    /// Whether the provider still offers a model (FR-CAT-5). Of one that it does not, also when
    /// it last listed it: the model stays in the catalog, with what was known about it.
    /// </summary>
    public static string Status(CatalogModel model, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Status switch
        {
            AiModelStatus.Active => "Active",
            AiModelStatus.Unavailable => $"Unavailable. Last listed on {times.Day(model.LastSeenAt)}.",
            _ => throw new ArgumentOutOfRangeException(nameof(model), model.Status, "The status has no text."),
        };
    }

    /// <summary>When the catalog of a provider was last synced, or that it never was.</summary>
    public static string Synced(ProviderCatalog catalog, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.LastSyncedAt is { } at ? $"Last synced {times.At(at)}." : "Not synced yet.";
    }

    /// <summary>
    /// What is said when the latest sync of a provider failed, or null when it did not: the list
    /// is then as the last sync that succeeded left it (FR-CAT-7).
    /// </summary>
    public static string? SyncFailed(ProviderCatalog catalog, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.LastSyncFailedAt is not { } at)
        {
            return null;
        }

        return catalog.LastSyncedAt is null
            ? $"A sync on {times.At(at)} failed, so the catalog of {catalog.ProviderName} is still empty."
            : $"A later sync, on {times.At(at)}, failed. The list is as it was before.";
    }
}
