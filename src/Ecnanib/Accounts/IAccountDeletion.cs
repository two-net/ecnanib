using Ecnanib.Platform.Persistence;

namespace Ecnanib.Accounts;

/// <summary>
/// Deleting an account: a user deletes their own, and every row that belongs to it goes with it,
/// at once (FR-ACC-9, NFR-SEC-9). It is a sensitive action, so it is done only for a user who has
/// just proved again who they are (FR-ACC-4), and only while every agent of theirs is stopped.
/// See docs/accounts.md.
/// </summary>
/// <remarks>
/// Each method is for the user who is signed in on the request it is given, and fails for a
/// request that is not signed in.
/// </remarks>
public interface IAccountDeletion
{
    /// <summary>Where the user stands with deleting their account, for the page that offers it.</summary>
    Task<AccountDeletionState> StateAsync(HttpContext request);

    /// <summary>
    /// Deletes the account of the user, with everything that belongs to it: what they accepted,
    /// their sessions on every browser, their two-factor authentication, their events in the
    /// audit trail, and whatever a later component keeps for them, stored keys included. The
    /// browser of the request is signed out, and a last message goes to the address.
    /// </summary>
    /// <param name="request">The request that posted the form. Its response loses the session cookie.</param>
    /// <returns>
    /// <see cref="AccountDeletionOutcome.Deleted"/> when the account is gone. Anything else means
    /// that nothing was deleted.
    /// </returns>
    Task<AccountDeletionOutcome> DeleteAsync(HttpContext request);
}

/// <summary>Where a user stands with deleting their account.</summary>
/// <param name="IsRecent">
/// Whether they proved who they are again on this browser, recently enough. Without it the page
/// sends them to <see cref="SessionAuthentication.ReauthenticationPath"/>.
/// </param>
/// <param name="AgentsNotStopped">
/// Whether an agent of theirs is not stopped, which stands in the way. It is asked only for a
/// user who has given the proof, and is false for one who has not.
/// </param>
public sealed record AccountDeletionState(bool IsRecent, bool AgentsNotStopped);

/// <summary>How deleting an account ended.</summary>
public enum AccountDeletionOutcome
{
    /// <summary>
    /// The account is gone, with everything that belonged to it. Also the answer when it was
    /// gone already: a form that is sent twice asks for what the first one did.
    /// </summary>
    Deleted,

    /// <summary>
    /// The user has not proved again who they are on this browser, or did so too long ago.
    /// Nothing was deleted.
    /// </summary>
    ConfirmationNeeded,

    /// <summary>An agent of the user is not stopped. Nothing was deleted.</summary>
    AgentsNotStopped,
}

/// <summary>
/// What Accounts asks about the agents of a user before it deletes the account: an account is
/// deleted only when all of its agents are stopped (FR-ACC-9). Accounts depends on no other
/// component, so it declares this interface, and Agents implements and registers it. See "When a
/// lower component must trigger a higher one" in docs/architecture.md.
/// </summary>
/// <remarks>
/// <para>
/// Nothing implements it until agents are built. Until then no user has an agent, and nothing
/// stands in the way of a deletion.
/// </para>
/// <para>
/// It is asked inside the transaction that deletes the account, which holds a lock on the row of
/// the account. That lock keeps a new agent from being created, and does not keep one that
/// exists from being started. So <b>an implementation locks the rows of the agents it reads</b>,
/// for the rest of the transaction (<c>FOR UPDATE</c>): then an agent that is started at the same
/// moment either is seen as not stopped, or waits and finds its account gone.
/// </para>
/// </remarks>
public interface IAgentActivity
{
    /// <summary>Whether a user has an agent that is not stopped.</summary>
    /// <param name="context">
    /// The context that holds the transaction. The rows that are read are locked in it, and
    /// nothing is committed.
    /// </param>
    /// <param name="userId">The user whose agents are asked about.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<bool> HasAgentsNotStoppedAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken);
}
