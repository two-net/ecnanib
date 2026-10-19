namespace Ecnanib.Accounts.Abstractions;

/// <summary>
/// Registration, sign-in, password change, account deletion and disclaimer acceptance (FR-ACC-01..04, FR-ACC-08).
/// HTTP-agnostic: the caller issues or ends the cookie session from the returned user. <paramref name="clientAddress"/>
/// is the remote address used by the sign-in rate limit (NFR-SEC-01); it may be null.
/// </summary>
public interface IAccountService
{
    Task<RegistrationResult> RegisterAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken);

    Task<AuthenticationResult> AuthenticateAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken);

    Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string? currentPassword, string? newPassword, string? clientAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the account after the password is entered again (the explicit confirmation of FR-ACC-04). Requires every
    /// agent to be Stopped or Draft and removes the user's keys, agents, decisions, trades and log entries in one transaction.
    /// </summary>
    Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, string? password, string? clientAddress, CancellationToken cancellationToken);

    Task<DisclaimerStatus> GetDisclaimerStatusAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Records acceptance of the current disclaimer version with a timestamp (FR-ACC-08); accepting twice keeps the first.</summary>
    Task<DisclaimerStatus> AcceptDisclaimerAsync(Guid userId, CancellationToken cancellationToken);
}
