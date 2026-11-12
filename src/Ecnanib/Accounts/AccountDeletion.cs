using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>Deletes the account of a user who asks for it (FR-ACC-9). See docs/accounts.md.</summary>
/// <remarks>
/// <para>
/// The account is one row of <c>users</c>, and every row that belongs to a user hangs on it with
/// a foreign key that deletes the row with the account. So deleting that one row removes
/// everything, in one transaction: nothing of the user is left for a later job to clear away,
/// and stored keys are gone at once.
/// </para>
/// <para>
/// The transaction starts by locking the row for deletion, which is the strongest lock a row
/// has. Every other transaction that changes the account locks the row first as well, so it and
/// a deletion happen one after the other, and whichever comes second finds what the first left.
/// </para>
/// <para>
/// Nothing is recorded in the audit trail: the events of an account go with it. The log says
/// that an account was deleted, by its id.
/// </para>
/// </remarks>
internal sealed class AccountDeletion(
    IDbContextFactory<EcnanibDbContext> contexts,
    Reauthentication reauthentication,
    IEnumerable<IAgentActivity> agents,
    IAccountMailer mailer,
    ILogger<AccountDeletion> logger) : IAccountDeletion
{
    public async Task<AccountDeletionState> StateAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        if (!await reauthentication.IsRecentAsync(request, SensitiveAction.DeleteAccount))
        {
            return new AccountDeletionState(IsRecent: false, AgentsNotStopped: false);
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // In a transaction, because whoever answers locks the rows it reads. Nothing is changed
        // in it, and it is undone when the answer is in.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        return new AccountDeletionState(
            IsRecent: true, AgentsNotStopped: await HasAgentsNotStoppedAsync(context, userId, cancellationToken));
    }

    public async Task<AccountDeletionOutcome> DeleteAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row is locked from here to the end, as the first thing this transaction asks of
        // it, and with the lock of a deletion. A weaker lock would let a row that points at the
        // account be added while the account is being deleted, and would have to become this
        // one for the deletion itself.
        var address = await context.Database
            .SqlQuery<string>(
                $"""
                SELECT email AS "Value" FROM users
                WHERE id = {userId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (address.Count == 0)
        {
            // The account went since the request was signed in: the form was sent twice, or
            // sent from two browsers. It is what was asked for.
            SessionCookie.Clear(request.Response);

            return AccountDeletionOutcome.Deleted;
        }

        // Asked under the lock. A new password ends the session that holds the proof, and
        // changes the row, so it came before this or waits for it: an account is never deleted
        // on a proof that was given with a password which no longer is one.
        if (!await reauthentication.IsRecentAsync(context, request, SensitiveAction.DeleteAccount))
        {
            return AccountDeletionOutcome.ConfirmationNeeded;
        }

        if (await HasAgentsNotStoppedAsync(context, userId, cancellationToken))
        {
            logger.LogInformation("User {UserId} did not delete their account: an agent of theirs is not stopped", userId);

            return AccountDeletionOutcome.AgentsNotStopped;
        }

        // From here on nothing is canceled with the request.
        //
        // The sessions first, and the sign-ins that wait before them, which is the order every
        // other transaction ends them in. The deletion of the account would end them as well,
        // in an order of its own, and two transactions that lock the same rows in different
        // orders can each end up waiting for the other.
        var ended = await Sessions.EndAllAsync(context, userId, CancellationToken.None);

        // The one row, and with it every row that belongs to the user.
        await context.Set<User>()
            .Where(user => user.Id == userId)
            .ExecuteDeleteAsync(CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        // The session is gone, so the browser need not send its token again. A sign-in of the
        // browser that waits for a code is left alone: it may be one of another account.
        SessionCookie.Clear(request.Response);

        // The address is personal data, so the log names the account and not the address.
        logger.LogInformation("User {UserId} deleted their account, which ended {SessionCount} sessions", userId, ended);

        // The last use of the address, which is kept nowhere from here on. The account is gone
        // by now, so the message goes out even when whoever deleted it has closed the connection.
        try
        {
            await mailer.SendAccountDeletedNoticeAsync(address[0], CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The account is deleted all the same.
            logger.LogError(error, "The notice of a deleted account could not be sent");
        }

        return AccountDeletionOutcome.Deleted;
    }

    /// <summary>
    /// Whether an agent of a user is not stopped, as the components that keep agents say inside
    /// the transaction of the caller. None is registered until agents are built.
    /// </summary>
    private async Task<bool> HasAgentsNotStoppedAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        foreach (var activity in agents)
        {
            if (await activity.HasAgentsNotStoppedAsync(context, userId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
