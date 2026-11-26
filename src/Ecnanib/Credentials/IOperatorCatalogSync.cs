using Ecnanib.Catalog;

namespace Ecnanib.Credentials;

/// <summary>
/// Syncing the model catalog on demand, which an operator does (FR-CAT-2, FR-OPS-4). It is the
/// operator function in front of <see cref="ICatalogSync.SyncAsync"/>: it asks whether the user
/// of its request is an operator, records that the sync was asked for, and then has the Catalog
/// sync. See "On demand" in docs/catalog.md, and "Operators" in docs/ownership.md.
/// </summary>
/// <remarks>
/// It is in this component because of what it uses. The Catalog syncs, and may not use Accounts,
/// which keeps the audit trail: Credentials may use both, and is the component that already
/// tells the Catalog of the keys of users. No key of a user has a part in this: the sync is made
/// with the keys that the configuration file holds for the providers.
/// </remarks>
public interface IOperatorCatalogSync
{
    /// <summary>
    /// Syncs, now, the catalog of every provider that the configuration file holds a catalog key
    /// for, for the operator of a request, and records in the audit trail that they asked
    /// (FR-OPS-5). It waits for the syncs, which are not given up when the browser goes away.
    /// </summary>
    /// <param name="request">The request of the operator.</param>
    /// <returns>
    /// The sync of each such provider, as it is recorded, with the ones that failed among them.
    /// Empty when the file holds a key for no provider: nothing was synced, and nothing is
    /// recorded. Null when the user of the request is no operator, or has no account any more:
    /// nothing was asked of anyone.
    /// </returns>
    Task<IReadOnlyList<CatalogSyncRun>?> SyncAsync(HttpContext request);
}
