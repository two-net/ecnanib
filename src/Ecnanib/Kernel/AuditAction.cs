namespace Ecnanib.Kernel;

/// <summary>
/// What an audit record says happened (FR-AUD-01). Each issue adds the actions it owns. Records store
/// the name, so a value is never renamed, and the Web UI gives each one its text. See Audit in
/// docs/architecture.md.
/// </summary>
public enum AuditAction
{
    SignedIn,

    /// <summary>A wrong password, a locked account, or an address with no account.</summary>
    SignInFailed,

    SignedOut,

    /// <summary>A user ended one of their other sessions.</summary>
    SessionRevoked,

    PasswordChanged,

    /// <summary>The bootstrap secret verified an administrator's address.</summary>
    AdministratorConfirmed,

    /// <summary>Someone signed in with a listed address entered the wrong bootstrap secret.</summary>
    AdministratorConfirmationFailed,

    /// <summary>The application started with a different active master key, and began re-encrypting secrets.</summary>
    MasterKeyRotated,

    /// <summary>An emailed link proved the user reads their address, through verification or a reset.</summary>
    EmailVerified,

    /// <summary>Someone asked for a reset link to be emailed to the account's address. By a visitor.</summary>
    PasswordResetRequested,

    /// <summary>A reset link set a new password, and ended every session.</summary>
    PasswordReset,

    /// <summary>An administrator issued a reset link to hand to the user, because email is off.</summary>
    PasswordResetLinkIssued,
}
