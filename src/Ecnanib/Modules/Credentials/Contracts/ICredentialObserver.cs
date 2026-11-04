namespace Ecnanib.Modules.Credentials.Contracts;

/// <summary>
/// Told after a change to a credential commits: Catalog syncs a new or replaced AI key's models
/// (FR-CAT-03), and Agents (#23) will pause on a failure or deletion (FR-KEY-10, FR-KEY-11). Credentials
/// can't depend on either, so they implement this. It's called on the request that made the change, so it
/// must return at once, and it's in process, so a crash can lose it: each observer needs a schedule that
/// recovers what it missed. See Inversions in docs/architecture.md.
/// </summary>
public interface ICredentialObserver
{
    void OnChanged(CredentialChange change);
}

/// <summary>A committed change to a credential. It holds IDs and statuses, never a secret or a label.</summary>
/// <param name="Provider">An AI provider's name, or <see cref="CredentialProviders.Binance"/>.</param>
/// <param name="Status">Its status now, or null once it's deleted.</param>
/// <param name="PreviousStatus">Its status before the change, or null for a new credential.</param>
public sealed record CredentialChange(
    Guid UserId,
    Guid CredentialId,
    CredentialKind Kind,
    string Provider,
    CredentialChangeKind Change,
    CredentialStatus? Status,
    CredentialStatus? PreviousStatus);

public enum CredentialChangeKind
{
    /// <summary>Saved for the first time. A refused key is never saved, so it raises nothing.</summary>
    Added,

    /// <summary>Its key was replaced.</summary>
    Rotated,

    /// <summary>A check reached the provider and wrote its result, whether or not the status changed.</summary>
    Validated,

    Deleted,
}
