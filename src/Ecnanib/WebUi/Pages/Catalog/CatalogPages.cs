using System.Globalization;
using Ecnanib.Catalog;

namespace Ecnanib.WebUi.Pages.Catalog;

/// <summary>
/// What the pages about the catalog of models say, in one place: how a model is named, how what
/// its provider says about it is written, and what is written for what the provider does not
/// say, which is that it is unknown (FR-CAT-4, FR-CAT-10).
/// </summary>
public static class CatalogPages
{
    /// <summary>
    /// What is written in place of something that a provider does not say about a model.
    /// Nothing is guessed, and nothing is left blank: a blank would read as "none".
    /// </summary>
    public const string Unknown = "Unknown";

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
    public static string Day(DateTimeOffset? time) => time is { } known ? PageTimes.Day(known) : Unknown;

    /// <summary>Whether something holds of a model, in a word.</summary>
    public static string YesOrNo(bool holds) => holds ? "Yes" : "No";

    /// <summary>
    /// Whether the provider still offers a model (FR-CAT-5). Of one that it does not, also when
    /// it last listed it: the model stays in the catalog, with what was known about it.
    /// </summary>
    public static string Status(CatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.Status switch
        {
            AiModelStatus.Active => "Active",
            AiModelStatus.Unavailable => $"Unavailable. Last listed on {PageTimes.Day(model.LastSeenAt)}.",
            _ => throw new ArgumentOutOfRangeException(nameof(model), model.Status, "The status has no text."),
        };
    }

    /// <summary>When the catalog of a provider was last synced, or that it never was.</summary>
    public static string Synced(ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.LastSyncedAt is { } at ? $"Last synced {PageTimes.At(at)}." : "Not synced yet.";
    }

    /// <summary>
    /// What is said when the latest sync of a provider failed, or null when it did not: the list
    /// is then as the last sync that succeeded left it (FR-CAT-7).
    /// </summary>
    public static string? SyncFailed(ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.LastSyncFailedAt is not { } at)
        {
            return null;
        }

        return catalog.LastSyncedAt is null
            ? $"A sync on {PageTimes.At(at)} failed, so the catalog of {catalog.ProviderName} is still empty."
            : $"A later sync, on {PageTimes.At(at)}, failed. The list is as it was before.";
    }
}
