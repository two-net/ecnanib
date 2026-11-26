using Ecnanib.Platform.Persistence;

namespace Ecnanib.Accounts;

/// <summary>
/// Disabling and enabling the account of a user, which an operator does (FR-OPS-4). A disabled
/// account is not signed in: its sessions end as it is disabled, and its password starts none.
/// Nothing of it is deleted. See "Disabling an account" in docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is an operator function, about a user who is not the one signed in, and is
/// asked without <see cref="Ownership.OwnedBy{TEntity}"/>. Each member asks again whether the
/// user of its request is an operator, whatever calls it (NFR-SEC-3), and answers a request that
/// is not as it answers for an account that is not there. See "Operators" in docs/ownership.md.
/// </para>
/// <para>
/// Disabling and enabling are recorded in the audit trail, in the transaction that does them
/// (FR-OPS-5). Finding an account changes nothing, and is not recorded.
/// </para>
/// </remarks>
public interface IAccountDisabling
{
    /// <summary>
    /// The account of an email address, as an operator is shown it before they disable or
    /// enable it.
    /// </summary>
    /// <param name="request">The request of the operator.</param>
    /// <param name="email">The address, as typed. It is compared as an account stores its own.</param>
    /// <returns>Null when the address has no account, and when the user of the request is no operator.</returns>
    Task<OperatorFoundAccount?> FindAsync(HttpContext request, string email);

    /// <summary>The account of an id, as an operator is shown it: the page that found it by its address names it by its id from then on.</summary>
    /// <param name="request">The request of the operator.</param>
    /// <param name="accountId">The id of the account.</param>
    /// <returns>Null when there is no such account, and when the user of the request is no operator.</returns>
    Task<OperatorFoundAccount?> FindAsync(HttpContext request, Guid accountId);

    /// <summary>
    /// Disables an account: from then on it is not signed in. Its sessions and the sign-ins of it
    /// that wait for a code end in the same transaction, so every browser of its user is signed
    /// out at once, and whatever depends on the account is told through
    /// <see cref="IAccountDependents"/>. The account of an operator is not disabled.
    /// </summary>
    /// <param name="request">The request of the operator.</param>
    /// <param name="accountId">The id of the account.</param>
    Task<AccountSwitchOutcome> DisableAsync(HttpContext request, Guid accountId);

    /// <summary>
    /// Enables an account that is disabled: its user can sign in again. Nothing else is undone:
    /// an agent that the disabling moved waits for its user, who resumes it.
    /// </summary>
    /// <param name="request">The request of the operator.</param>
    /// <param name="accountId">The id of the account.</param>
    Task<AccountSwitchOutcome> EnableAsync(HttpContext request, Guid accountId);
}

/// <summary>
/// An account as an operator is shown it: what it takes to disable or enable the right one, and
/// nothing else of its user. It holds no secret (FR-OPS-5).
/// </summary>
/// <param name="Id">The id of the account, which names it from then on.</param>
/// <param name="Email">The address of the account, as it is stored.</param>
/// <param name="DisabledAt">When the account was disabled, or null while it is not.</param>
/// <param name="IsOperator">
/// Whether the account is an operator's: its address is listed in the configuration file and is
/// verified. Such an account is not disabled.
/// </param>
public sealed record OperatorFoundAccount(Guid Id, string Email, DateTimeOffset? DisabledAt, bool IsOperator);

/// <summary>How disabling or enabling an account ended.</summary>
public enum AccountSwitchOutcome
{
    /// <summary>The account was disabled, or enabled, and that is recorded in the audit trail.</summary>
    Done,

    /// <summary>
    /// The account was as asked for already: the form was sent twice, or another operator was
    /// first. Nothing was done, and nothing is recorded.
    /// </summary>
    AlreadySo,

    /// <summary>There is no such account: it never was, or it was deleted meanwhile.</summary>
    NotFound,

    /// <summary>
    /// The account is an operator's, and is not disabled: an operator who was disabled could
    /// not switch the global halt. Whoever is to be no operator is taken off the list of the
    /// configuration file first.
    /// </summary>
    IsOperator,

    /// <summary>The user of the request is no operator, or is not signed in. Nothing was done.</summary>
    Refused,
}

/// <summary>
/// What depends on an account being enabled, and is told in the transaction that disables it. A
/// component above Accounts implements it and registers the implementation: Agents does, for the
/// agents of the account that run. See docs/architecture.md.
/// </summary>
public interface IAccountDependents
{
    /// <summary>
    /// An operator disabled the account of a user (FR-OPS-4). It is called inside the transaction
    /// that disables the account, which holds the lock of the account's row: what it writes is
    /// stored when the account is disabled, and not when it is not.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user whose account was disabled.</param>
    /// <param name="cancellationToken">Cancels what the dependent does.</param>
    Task AccountDisabledAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken);
}
