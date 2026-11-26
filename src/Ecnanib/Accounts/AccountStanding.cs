using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// Whether an account is disabled, for what acts for its user and must not while it is
/// (FR-OPS-4). An operator disables and enables an account through <see cref="IAccountDisabling"/>.
/// See "Disabling an account" in docs/accounts.md.
/// </summary>
/// <remarks>
/// A disabled account is not signed in, so nothing that a request does for its user gets as far
/// as this. It is for what the lock of the account decides at the last moment: a request that
/// was signed in before the account was disabled, and waited for the lock behind what disabled
/// it. And it is for what the service does for a user by itself, such as ending a halt.
/// </remarks>
public static class AccountStanding
{
    /// <summary>
    /// Whether an account is disabled, as the context reads it now: inside the transaction of the
    /// caller, and so under the lock of the account when the caller holds it.
    /// </summary>
    /// <param name="context">The context of the caller.</param>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>False for an account that is not disabled, and for one that is gone: whether it is gone is another question.</returns>
    public static Task<bool> IsDisabledAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Set<User>().AnyAsync(user => user.Id == userId && user.DisabledAt != null, cancellationToken);
    }
}
