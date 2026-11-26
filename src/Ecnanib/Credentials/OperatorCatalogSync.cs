using Ecnanib.Accounts;
using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Credentials;

/// <summary>
/// Has the model catalog synced for an operator, and records that they asked (FR-CAT-2, FR-OPS-4,
/// FR-OPS-5). See <see cref="IOperatorCatalogSync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recorded when it is asked for, before the providers are asked.</b> The event is written in
/// a transaction of its own, which is over before the first provider is asked: a sync asks the
/// providers with no transaction open, and may take most of a minute. So the trail says who
/// asked and when, and the rows of the syncs, which the Catalog writes, say what came of it.
/// </para>
/// <para>
/// <b>Not given up with the request.</b> Once the event is written, the syncs run until they
/// are over or the application stops: an operator who closes the page leaves no request in the
/// trail that nothing was done for.
/// </para>
/// </remarks>
internal sealed class OperatorCatalogSync(
    ICatalogSync catalog,
    IDbContextFactory<EcnanibDbContext> contexts,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<OperatorCatalogSync> logger) : IOperatorCatalogSync
{
    public async Task<IReadOnlyList<CatalogSyncRun>?> SyncAsync(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Asked here as well as by the page: whatever calls this, only an operator has the
        // catalog synced (NFR-SEC-3). It is false for a request that is not signed in.
        if (!OperatorAccess.IsOperator(request.User))
        {
            logger.LogWarning(
                "User {UserId} was refused a sync of the model catalog, which is for an operator", SignedInUser.IdOf(request));

            return null;
        }

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        // Only the providers that the file holds a catalog key for are synced on demand. With
        // none there is nothing to do, so nothing is asked for, and nothing is recorded.
        var providers = await catalog.StatusAsync(cancellationToken);

        if (!providers.Any(provider => provider.HasCatalogKey))
        {
            logger.LogInformation(
                "User {UserId} asked for a sync of the model catalog, and the configuration file holds a catalog key for no provider: nothing is synced",
                userId);

            return [];
        }

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            // The account first, as everything that records an action of a user does: the event
            // points at it, and a deletion of the account comes before this or waits for it.
            if (await StoredKeys.LockAccountAsync(context, userId, cancellationToken) is null)
            {
                // The account was deleted, on another browser, since this request was signed in.
                return null;
            }

            await AuditTrail.RecordAsync(
                context,
                userId,
                AuditAction.CatalogSyncRequested,
                AuditTarget.ModelCatalog,
                clock.GetUtcNow(),
                AuditOrigin.Of(request),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("User {UserId} asked for a sync of the model catalog, outside its schedule", userId);

        // With the token of the application, and not that of the request: what was recorded as
        // asked for is done, also when the browser of the operator has gone. Each sync has its
        // own limit of time, and records how it ended.
        return await catalog.SyncAsync(lifetime.ApplicationStopping);
    }
}
