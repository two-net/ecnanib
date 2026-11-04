using System.Security.Claims;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// Proving that a user reads their address (FR-AUTH-03), which only happens when email is enabled.
/// A link verifies only when it's opened by someone signed in to its account, who therefore knows that
/// account's password: see Verification and resets in docs/architecture.md.
/// </summary>
public interface IEmailVerification
{
    /// <summary>Email:Enabled. With it off, nothing is verified, and nothing needs to be (EXT-EML-01).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether the user meets the email requirement for switching an agent to Live (FR-AUTH-03,
    /// FR-AGT-05): true when email is off or their address is verified. False for an unknown user.
    /// </summary>
    Task<bool> IsSatisfiedAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Emails the user a new link, which replaces any earlier one. One account gets at most one every few
    /// minutes, whoever asks, and a request inside that window sends nothing and keeps the earlier link.
    /// </summary>
    Task<VerificationEmailResult> SendAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the address of <paramref name="user"/>'s account if <paramref name="token"/> is that
    /// account's live link, which then stops working. A link for another account is left alone, so it
    /// still works for the account it was sent to. Pass it over HTTP, never over a circuit.
    /// </summary>
    Task<EmailVerificationResult> VerifyAsync(ClaimsPrincipal user, string? token, CancellationToken cancellationToken);
}

public enum VerificationEmailResult
{
    Sent,
    AlreadyVerified,

    /// <summary>One was sent a few minutes ago. It still works.</summary>
    TooSoon,

    /// <summary>Email is off, so there's nothing to send.</summary>
    Disabled,
}

public enum EmailVerificationResult
{
    Verified,

    /// <summary>Expired, used, another account's, or never issued. Each gets this one answer.</summary>
    InvalidLink,
}
