using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Catalog;

/// <summary>What a change to a credential means for the catalog.</summary>
internal enum CredentialAction
{
    /// <summary>List the key's models (FR-CAT-03).</summary>
    Sync,

    /// <summary>The key isn't valid any more, so it can't use what it could.</summary>
    MarkUnavailable,

    /// <summary>The key is gone, and so are its rows.</summary>
    Remove,
}

/// <summary>
/// Syncs a key when it's added or replaced, or when a check finds it valid again, and clears it when it
/// stops being valid or is deleted (FR-CAT-03). It runs as a job so the request that changed the key doesn't
/// wait. A job is lost with the process, and the schedule recovers it: see Inversions in docs/architecture.md.
/// </summary>
internal sealed class CatalogCredentialObserver(CatalogSync sync, IJobRunner jobs) : ICredentialObserver
{
    public void OnChanged(CredentialChange change)
    {
        if (change.Kind != CredentialKind.Ai || ActionFor(change) is not { } action)
        {
            return;
        }

        // RunAsync never throws for the work's failure, so the task can be left to run.
        _ = jobs.RunAsync(
            CatalogSync.CredentialJobName, CatalogSync.OnDemandTimeout, (context, token) => sync.ApplyAsync(change, action, context, token));
    }

    /// <summary>
    /// Null when nothing changes for the catalog, such as a check that leaves a key valid, which already
    /// listed its models to validate it, so it isn't asked a second time.
    /// </summary>
    public static CredentialAction? ActionFor(CredentialChange change) => change switch
    {
        { Change: CredentialChangeKind.Deleted } => CredentialAction.Remove,
        { Status: CredentialStatus.Valid, Change: CredentialChangeKind.Added or CredentialChangeKind.Rotated } => CredentialAction.Sync,
        { Status: CredentialStatus.Valid, PreviousStatus: not CredentialStatus.Valid } => CredentialAction.Sync,
        { Status: not CredentialStatus.Valid, PreviousStatus: CredentialStatus.Valid } => CredentialAction.MarkUnavailable,
        { Status: not CredentialStatus.Valid, Change: CredentialChangeKind.Rotated } => CredentialAction.MarkUnavailable,
        _ => null,
    };
}
