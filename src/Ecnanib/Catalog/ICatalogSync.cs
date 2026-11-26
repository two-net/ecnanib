using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// Syncs the catalog of models from the providers' own lists, outside the schedule (FR-CAT-2,
/// FR-CAT-3), and says which models a provider lists to a user's key (FR-CAT-9). The schedule is
/// the job's, which this component registers. See docs/catalog.md.
/// </summary>
public interface ICatalogSync
{
    /// <summary>
    /// Syncs, now, the catalog of every provider that the configuration file holds a catalog key
    /// for. It is what an operator's request for a sync comes to.
    /// </summary>
    /// <param name="cancellationToken">Cancels the syncs.</param>
    /// <returns>
    /// The sync of each such provider, as it is recorded. A provider whose sync failed is among
    /// them, with why, and its catalog is as it was: one provider's failure is not another's
    /// (FR-CAT-7). A failed sync that could not be recorded either is among them all the same,
    /// and in the log. A provider without a catalog key is not synced here, and is not among them.
    /// </returns>
    Task<IReadOnlyList<CatalogSyncRun>> SyncAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Tells the catalog that a provider took a user's key, and answers with the models that the
    /// provider lists to that key (FR-CAT-9). The provider is asked for its list with the key.
    /// When the configuration file holds no catalog key for the provider, the provider's catalog
    /// is refreshed with that list (FR-CAT-3). With a catalog key, the catalog stays as it is,
    /// and no sync is recorded.
    /// </summary>
    /// <param name="provider">The provider that took the key.</param>
    /// <param name="apiKey">
    /// The user's key. It is handed to the gateway for the one list, and is kept nowhere: not in
    /// the catalog, not in a log event, and not in an error.
    /// </param>
    /// <param name="cancellationToken">Cancels the list, and the refresh.</param>
    /// <returns>
    /// The identifiers of the models that the provider listed to the key, as the provider writes
    /// them, or null when it is not known what the key can use: the provider gave no list, or
    /// was not asked, because a list of it ran out of time a moment ago. A list is never empty.
    /// Whose key it is the catalog is not told: the caller keeps the answer with the key. The
    /// task fails only when the caller cancels: a provider that does not list its models, and a
    /// catalog that cannot be written, are the catalog's own to record and to log, and say
    /// nothing about the key.
    /// </returns>
    Task<IReadOnlyList<string>?> KeyValidatedAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// How the sync of each provider's catalog stands: its latest sync, with how it ended, and
    /// the latest one that succeeded. It is the last catalog sync per provider that an operator
    /// status screen shows (FR-OPS-2). It reads the syncs that are recorded, and asks no
    /// provider.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// Every provider of the application, by its code, also one that was never synced. A row
    /// of a sync names no user and no key (FR-CAT-7), so neither does this.
    /// </returns>
    Task<IReadOnlyList<ProviderSyncStatus>> StatusAsync(CancellationToken cancellationToken);
}

/// <summary>How the sync of one provider's catalog stands.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderName">What the provider is called.</param>
/// <param name="HasCatalogKey">
/// Whether the configuration file holds a key of the service for the provider. With one, the
/// catalog of the provider is synced on the schedule and on demand. Without one, it is synced
/// only when the provider takes the key of a user (FR-CAT-3). It says that there is a key, and
/// nothing of the key.
/// </param>
/// <param name="Latest">The latest sync, however it ended, or null when the provider was never synced.</param>
/// <param name="LatestSucceeded">
/// The latest sync that succeeded, which is what the catalog of the provider holds, or null when
/// none did. It is <paramref name="Latest"/> when that one succeeded.
/// </param>
public sealed record ProviderSyncStatus(
    AiProviderCode Provider, string ProviderName, bool HasCatalogKey, CatalogSyncRun? Latest, CatalogSyncRun? LatestSucceeded);
