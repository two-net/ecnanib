using System.Globalization;

namespace Ecnanib.WebUi.Pages.Operator;

/// <summary>
/// The texts of the page on which an operator has the model catalog synced, outside its
/// schedule, in one place (NFR-USA-4, FR-CAT-2, FR-OPS-4). The operator status screen leads to
/// it. The table of the syncs has its texts in <see cref="OperatorStatusPages"/>, as it has on
/// that screen. See "Operators" in docs/ownership.md.
/// </summary>
public static class OperatorSyncPages
{
    /// <summary>The name of the form that has the catalog synced.</summary>
    public const string SyncForm = "sync-catalog";

    /// <summary>
    /// The title, in the browser, of the page, its heading, and the text of the link that leads
    /// to it from the operator status screen.
    /// </summary>
    public static string Title => "Sync the model catalog";

    /// <summary>What a sync does, and with which key (FR-CAT-1, FR-CAT-3).</summary>
    public static string Explained =>
        "A sync asks each AI provider for the list of its models, and stores what the list says. It is made with the key " +
        "that the configuration file holds for the provider, so only the providers that the file holds a key for are " +
        "synced here.";

    /// <summary>
    /// That a sync on demand counts as one of the schedule does, and what follows from a model
    /// that the lists lack often enough (FR-CAT-5, FR-CAT-8).
    /// </summary>
    /// <param name="missedSyncs">How many syncs in a row may lack a model before it is marked unavailable.</param>
    public static string Counts(int missedSyncs)
    {
        var lacks = missedSyncs == 1
            ? "lacks in 1 sync"
            : $"lacks in {missedSyncs.ToString(CultureInfo.InvariantCulture)} syncs in a row";

        return $"A sync on demand counts as one of the schedule does: a model that the list of its provider {lacks} is " +
            "marked unavailable, and an agent that runs with it then waits for its user.";
    }

    /// <summary>What having the catalog synced does, before the button.</summary>
    public static string SyncMeans =>
        "The page waits for the sync, which takes up to a minute. A provider that fails leaves its catalog as it was, and " +
        "the others alone.";

    /// <summary>The text of the button that has the catalog synced.</summary>
    public static string Sync => "Sync the catalog now";

    /// <summary>Why the page has no button when the configuration file holds a catalog key for no provider.</summary>
    public static string NothingToSync =>
        "The configuration file holds a key for no provider, so there is nothing to sync on demand: the catalog of each " +
        "provider is synced when the key of a user for it is checked.";

    /// <summary>What the page says after a sync that was asked for.</summary>
    public static string Done => "The sync that you asked for is over. The table says how it ended for each provider.";

    /// <summary>That a sync that an operator asks for is recorded, said under the button (FR-OPS-5).</summary>
    public static string Recorded => "Each sync that an operator asks for is recorded in the audit trail, with who asked and when.";
}
