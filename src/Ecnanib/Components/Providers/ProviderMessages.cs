using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;

namespace Ecnanib.Components.Providers;

/// <summary>User-facing texts of the providers page (FR-SYNC-02, FR-SYNC-06).</summary>
internal static class ProviderMessages
{
    public const string Intro =
        "Each provider's model catalog is synced from the provider's own model list with a stored key: when a key is validated, on the configured schedule, and on demand here. The catalog is shared by all users. A model the provider stops listing is marked deprecated, and agents still using one are warned on their page.";

    public const string SyncHint =
        "Sync now uses your own valid key for the provider and nobody else's; the scheduled sync uses any valid stored key. Without a key of your own, add one on the keys page to sync.";

    public const string AddYourKey = "Add your key to sync";

    public const string ChooseProvider = "Choose a provider to sync.";

    public static string Synced(AiProvider provider, int listed, int deprecated) =>
        $"{ProviderNames.Display(provider)} was synced: {Plural(listed, "model", "models")} listed"
        + (deprecated > 0 ? $", {Plural(deprecated, "model", "models")} newly deprecated." : ".");

    public static string NoKey(AiProvider provider) =>
        $"You have no valid {ProviderNames.Display(provider)} key, so you cannot sync its catalog. Add one on the keys page.";

    public static string Failed(AiProvider provider, string? error) =>
        $"The {ProviderNames.Display(provider)} sync failed{Detail(error)} The catalog is unchanged; try again.";

    public static string NoModels(AiProvider provider) =>
        $"No {ProviderNames.Display(provider)} model is synced yet. Add a {ProviderNames.Display(provider)} key on the keys page; validating it fills the catalog.";

    public static string Trigger(SyncTrigger? trigger) => trigger switch
    {
        SyncTrigger.KeyValidation => "key validation",
        SyncTrigger.Scheduled => "scheduled",
        SyncTrigger.Manual => "manual",
        _ => "",
    };

    public static string StatusClass(SyncStatus status) => "status-" + status.ToString().ToLowerInvariant();

    public static string StatusClass(ModelStatus status) => "status-" + status.ToString().ToLowerInvariant();

    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private static string Detail(string? detail) => string.IsNullOrWhiteSpace(detail) ? "." : $": {detail.TrimEnd('.')}.";
}
