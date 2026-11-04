namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// Resetting a forgotten password through a single-use, time-limited link (FR-AUTH-06). With email on,
/// users ask for one by address. With it off, an administrator issues one to hand over out of band
/// (EXT-EML-01). See Verification and resets in docs/architecture.md.
/// </summary>
public interface IPasswordResets
{
    PasswordResetMode Mode { get; }

    /// <summary>
    /// Emails a reset link if an account uses <paramref name="email"/>. The caller can't tell whether
    /// one did, or whether anything was sent: show the same answer whatever happens. One account gets at
    /// most one every few minutes, whoever asks. Does nothing with email off.
    /// </summary>
    Task RequestAsync(string email, CancellationToken cancellationToken);

    /// <summary>Whether a link would still work, without using it, so a page can say so before asking for a password.</summary>
    Task<bool> IsLiveAsync(string? token, CancellationToken cancellationToken);

    /// <summary>
    /// Sets a new password through a live link, which then stops working. It ends every session of the
    /// account and clears any lockout. A link that came by email also verifies the address, because only
    /// its reader could have used it. Check the new password's length here, not in the page.
    /// </summary>
    Task<PasswordResetResult> ResetAsync(string? token, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    /// A link for the account that uses <paramref name="email"/>, for an administrator to hand over,
    /// when email is off. It replaces any earlier one. Refused unless <paramref name="administratorId"/>
    /// is an administrator now, which is checked here because a circuit never reaches middleware.
    /// </summary>
    Task<ResetLinkResult> IssueAsync(Guid administratorId, string email, CancellationToken cancellationToken);
}

public enum PasswordResetMode
{
    /// <summary>Users ask for a link, which goes to their address.</summary>
    ByEmail,

    /// <summary>Email is off, so an administrator issues the link.</summary>
    ByAdministrator,
}

public enum PasswordResetResult
{
    Reset,

    /// <summary>Expired, used, or never issued. Each gets this one answer.</summary>
    InvalidLink,

    PasswordTooShort,
    PasswordTooLong,
}

public enum ResetLinkOutcome
{
    Issued,
    NoAccount,

    /// <summary>The caller isn't an administrator, or email is on, when users ask for their own.</summary>
    NotAllowed,
}

/// <summary>
/// A class, not a record, because a record's ToString would print the link, which is as good as the
/// account's password until it's used or expires.
/// </summary>
public sealed class ResetLinkResult
{
    public required ResetLinkOutcome Outcome { get; init; }

    /// <summary>
    /// Set when <see cref="Outcome"/> is <see cref="ResetLinkOutcome.Issued"/>. It starts with
    /// App:PublicBaseUrl, or is a path when that isn't set.
    /// </summary>
    public string? Link { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public override string ToString() => Outcome.ToString();
}
