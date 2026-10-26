using Ecnanib.Ui.Abstractions;

namespace Ecnanib.Accounts.Abstractions;

/// <summary>
/// Registration, sign-in, password change, account deletion, disclaimer acceptance and the theme and animation
/// preference (FR-ACC-01..04, FR-ACC-08, FR-UI-02, FR-UI-03).
/// HTTP-agnostic: the caller issues or ends the cookie session from the returned user. <paramref name="clientAddress"/>
/// is the remote address used by the sign-in rate limit (NFR-SEC-01); it may be null.
/// </summary>
public interface IAccountService
{
    Task<RegistrationResult> RegisterAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken);

    Task<AuthenticationResult> AuthenticateAsync(string? email, string? password, string? clientAddress, CancellationToken cancellationToken);

    Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string? currentPassword, string? newPassword, string? clientAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Ends every session of the user on the server (NFR-SEC-01): the security stamp is rotated, so a ticket copied
    /// before this is rejected at its next check, and the in-process revocation ends it at once. The caller deletes the
    /// browser's cookie. Nothing happens for a user that no longer exists.
    /// </summary>
    Task SignOutAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the account after the password is entered again (the explicit confirmation of FR-ACC-04). Requires every
    /// agent to be Stopped or Draft and removes the user's keys, agents, decisions, trades and log entries in one transaction.
    /// </summary>
    Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, string? password, string? clientAddress, CancellationToken cancellationToken);

    Task<DisclaimerStatus> GetDisclaimerStatusAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Records acceptance of the current disclaimer version with a timestamp (FR-ACC-08); accepting twice keeps the first.</summary>
    Task<DisclaimerStatus> AcceptDisclaimerAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's theme and animation preference, persisted per user (FR-UI-02, FR-UI-03); null when the user is gone.</summary>
    Task<UserPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stores the preference; false when the user is gone.</summary>
    Task<bool> UpdatePreferencesAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken);
}
