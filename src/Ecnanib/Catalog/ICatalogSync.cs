using Ecnanib.Shared;

namespace Ecnanib.Catalog;

/// <summary>
/// Syncs the catalog of models from the providers' own lists, outside the schedule (FR-CAT-2,
/// FR-CAT-3). The schedule is the job's, which this component registers. See docs/catalog.md.
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
    /// Tells the catalog that a provider took a user's key. When the configuration file holds no
    /// catalog key for the provider, the provider's catalog is refreshed with the user's key
    /// (FR-CAT-3). With a catalog key, nothing is done.
    /// </summary>
    /// <param name="provider">The provider that took the key.</param>
    /// <param name="apiKey">
    /// The user's key. It is handed to the gateway for the one list, and is kept nowhere: not in
    /// the catalog, not in a log event, and not in an error.
    /// </param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>
    /// A task that ends when the refresh has. It fails only when the caller cancels: a provider
    /// that does not list its models, and a catalog that cannot be written, are the catalog's own
    /// to record and to log, and say nothing about the key.
    /// </returns>
    Task KeyValidatedAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken);
}
