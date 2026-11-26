using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// Disables and enables the account of a user for an operator, and finds the account that an
/// operator means (FR-OPS-4). See <see cref="IAccountDisabling"/>, and "Disabling an account" in
/// docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is asked again.</b> The pages that call this carry the operator policy, and
/// each member asks <see cref="OperatorAccess.IsOperator"/> of the user of its request all the
/// same: whatever calls it, nobody else finds, disables, or enables an account (NFR-SEC-3).
/// </para>
/// <para>
/// <b>One transaction, under the locks of both accounts.</b> The row of the operator is locked
/// as everything that records an action of a user locks it, and the row of the account that is
/// disabled as everything that acts for its user does: a sign-in, a start of an agent, and a
/// change to a key each wait for this, or this waits for them. The two rows are locked in one
/// statement, in the order of their ids, which is the order that a catalog sync locks the
/// accounts of many users in. So nothing here waits for something that waits for it.
/// </para>
/// <para>
/// <b>The event belongs to the operator.</b> It carries the operator as its user, and names the
/// other account as its target (FR-OPS-5). It goes with the account of the operator, which is
/// not deleted while they are one, and stays when the account that it names is deleted: the id
/// then names nobody.
/// </para>
/// </remarks>
internal sealed class AccountDisabling(
    IDbContextFactory<EcnanibDbContext> contexts,
    Operators operators,
    IEnumerable<IAccountDependents> dependents,
    TimeProvider clock,
    ILogger<AccountDisabling> logger) : IAccountDisabling
{
    public async Task<OperatorFoundAccount?> FindAsync(HttpContext request, string email)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(email);

        if (!MayAsk(request))
        {
            return null;
        }

        // As an account stores its address, so that it is found however the operator types it.
        var address = EmailAddress.Normalize(email);

        if (!EmailAddress.LooksValid(address))
        {
            return null;
        }

        await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);

        return await ShownAsync(context.Set<User>().Where(user => user.Email == address), request.RequestAborted);
    }

    public async Task<OperatorFoundAccount?> FindAsync(HttpContext request, Guid accountId)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MayAsk(request))
        {
            return null;
        }

        await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);

        return await ShownAsync(context.Set<User>().Where(user => user.Id == accountId), request.RequestAborted);
    }

    public Task<AccountSwitchOutcome> DisableAsync(HttpContext request, Guid accountId) => SwitchAsync(request, accountId, disable: true);

    public Task<AccountSwitchOutcome> EnableAsync(HttpContext request, Guid accountId) => SwitchAsync(request, accountId, disable: false);

    private async Task<AccountSwitchOutcome> SwitchAsync(HttpContext request, Guid accountId, bool disable)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MayAsk(request))
        {
            return AccountSwitchOutcome.Refused;
        }

        var operatorId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Both rows, locked in the order of their ids as the database orders them, for the rest
        // of the transaction. An operator who names their own account names one row.
        var locked = await context.Database
            .SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM users
                WHERE id IN ({operatorId}, {accountId})
                ORDER BY id
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (!locked.Contains(operatorId))
        {
            // The account of the operator was deleted, on another browser, since this request was signed in.
            return AccountSwitchOutcome.Refused;
        }

        if (!locked.Contains(accountId))
        {
            return AccountSwitchOutcome.NotFound;
        }

        // Read under the lock: this is the account until the transaction ends.
        var account = await context.Set<User>()
            .AsNoTracking()
            .Where(user => user.Id == accountId)
            .Select(user => new { user.Email, IsVerified = user.EmailVerifiedAt != null })
            .SingleAsync(cancellationToken);

        if (disable && operators.Includes(account.Email, account.IsVerified))
        {
            // Their own account included: an operator who is disabled switches no global halt.
            logger.LogInformation(
                "User {UserId} was refused disabling the account of user {AccountId}, which is an operator's", operatorId, accountId);

            return AccountSwitchOutcome.IsOperator;
        }

        var now = clock.GetUtcNow();

        // Written only where it changes the account, so the answer says whether this request
        // did it. Of two requests at once the second waits for the row, and finds the account
        // as the first one left it.
        var switched = disable
            ? await context.Set<User>()
                .Where(user => user.Id == accountId && user.DisabledAt == null)
                .ExecuteUpdateAsync(row => row.SetProperty(user => user.DisabledAt, (DateTimeOffset?)now), cancellationToken)
            : await context.Set<User>()
                .Where(user => user.Id == accountId && user.DisabledAt != null)
                .ExecuteUpdateAsync(row => row.SetProperty(user => user.DisabledAt, (DateTimeOffset?)null), cancellationToken);

        if (switched == 0)
        {
            // It was as asked for: the form was sent twice, or another operator was first.
            // Nothing was done, so nothing is recorded.
            return AccountSwitchOutcome.AlreadySo;
        }

        var ended = 0;

        if (disable)
        {
            // Every browser of the user is signed out with the change, and a sign-in of theirs
            // that waits for a code ends: no session outlives the account being enabled.
            ended = await Sessions.EndAllAsync(context, accountId, cancellationToken);

            // What depends on the account is told before the transaction ends: what it changes
            // is stored when the account is disabled, and not when it is not.
            foreach (var dependent in dependents)
            {
                await dependent.AccountDisabledAsync(context, accountId, cancellationToken);
            }
        }

        await AuditTrail.RecordAsync(
            context,
            operatorId,
            disable ? AuditAction.AccountDisabled : AuditAction.AccountEnabled,
            AuditTarget.Account(accountId),
            now,
            AuditOrigin.Of(request),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // The address is personal data, so the log names both accounts by their ids.
        if (disable)
        {
            logger.LogInformation(
                "User {UserId} disabled the account of user {AccountId}: it is not signed in until it is enabled, and {SessionCount} of its sessions ended",
                operatorId,
                accountId,
                ended);
        }
        else
        {
            logger.LogInformation("User {UserId} enabled the account of user {AccountId}: its user can sign in again", operatorId, accountId);
        }

        return AccountSwitchOutcome.Done;
    }

    /// <summary>Whether the user of a request is an operator, which everything here is for. It says so in the log when not.</summary>
    private bool MayAsk(HttpContext request)
    {
        if (OperatorAccess.IsOperator(request.User))
        {
            return true;
        }

        logger.LogWarning(
            "User {UserId} was refused finding, disabling, or enabling an account, which is for an operator", SignedInUser.IdOf(request));

        return false;
    }

    /// <summary>An account as an operator is shown it, or null when there is none.</summary>
    private async Task<OperatorFoundAccount?> ShownAsync(IQueryable<User> accounts, CancellationToken cancellationToken)
    {
        var account = await accounts
            .AsNoTracking()
            .Select(user => new { user.Id, user.Email, user.DisabledAt, IsVerified = user.EmailVerifiedAt != null })
            .SingleOrDefaultAsync(cancellationToken);

        return account is null
            ? null
            : new OperatorFoundAccount(account.Id, account.Email, account.DisabledAt, operators.Includes(account.Email, account.IsVerified));
    }
}
