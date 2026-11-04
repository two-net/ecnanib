using System.Security.Claims;

namespace Ecnanib.Modules.Identity.Contracts;

/// <summary>
/// Accounts (SRS 3.1). This returns principals and leaves the cookie to the caller, because only an
/// HTTP response can set one. Pass secrets over HTTP, never over a circuit: see docs/architecture.md.
/// </summary>
public interface IAccounts
{
    /// <summary>What the registration form needs to show.</summary>
    RegistrationRules RegistrationRules { get; }

    /// <summary>
    /// Empty when the submission was valid, which says nothing about whether an account was created:
    /// an address that's taken gets the same answer, so that this alone can't look one up. Signing in
    /// afterwards can still tell: see Registration in docs/architecture.md. With email on, a new account
    /// is sent a link to verify its address, in the background.
    /// </summary>
    Task<IReadOnlyList<RegistrationProblem>> RegisterAsync(Registration registration, CancellationToken cancellationToken);

    /// <summary>
    /// An unknown address, a wrong password and a locked account are one result,
    /// <see cref="SignInOutcome.Refused"/>, and each spends one hash, so tell the user no more than that.
    /// Otherwise it starts a session, which <paramref name="client"/> describes in the user's list of
    /// them. With two-factor on, it starts no session and returns a challenge for
    /// <see cref="CompleteSignInAsync"/> instead.
    /// </summary>
    Task<SignInResult> SignInAsync(string email, string password, ClientDetails client, CancellationToken cancellationToken);

    /// <summary>
    /// The second step of a sign-in with two-factor on: a code from the authenticator, or a recovery
    /// code, for the challenge the first step returned. A wrong code counts towards the same lockout as a
    /// wrong password, and gets the same answer as a locked account. The challenge works until it's used
    /// or expires, or a password change ends it.
    /// </summary>
    Task<SignInResult> CompleteSignInAsync(string? challenge, string code, ClientDetails client, CancellationToken cancellationToken);

    /// <summary>Whether a challenge would still work, so the second step's page can send someone back to sign in again.</summary>
    Task<bool> IsChallengeLiveAsync(string? challenge, CancellationToken cancellationToken);

    /// <summary>
    /// Records acceptance of the versions the user was shown. Null, recording nothing, unless both are
    /// current. Otherwise the principal to sign in, which replaces the one in the cookie and keeps its
    /// session.
    /// </summary>
    Task<ClaimsPrincipal?> AcceptLegalDocumentsAsync(
        ClaimsPrincipal user,
        string? termsOfUseVersion,
        string? riskDisclosureVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// FR-AUTH-08. Revokes every session but <paramref name="user"/>'s own, and any outstanding reset
    /// link. A guess at the current password counts towards the same lockout as a sign-in, and a locked
    /// account gets the answer a wrong password gets. Check the new password's length here, not in the page.
    /// </summary>
    Task<PasswordChangeResult> ChangePasswordAsync(
        ClaimsPrincipal user,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);

    /// <summary>Null when the account no longer exists.</summary>
    Task<AccountOverview?> FindAsync(Guid userId, CancellationToken cancellationToken);
}

/// <param name="PasswordMinLength">The shortest password accepted (NFR-SEC-02).</param>
/// <param name="PasswordMaxLength">The longest password accepted.</param>
/// <param name="AsksForCountry">Whether the operator blocks any country, which is the only reason to ask (NFR-LEG-04).</param>
public sealed record RegistrationRules(int PasswordMinLength, int PasswordMaxLength, bool AsksForCountry);

/// <summary>
/// A class, not a record, because a record's ToString would print the password. The version properties
/// hold what the user was shown and accepted, and are null for a document they didn't accept.
/// </summary>
public sealed class Registration
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    public string? Country { get; init; }

    public string? TermsOfUseVersion { get; init; }

    public string? RiskDisclosureVersion { get; init; }
}

public enum RegistrationProblem
{
    InvalidEmail,
    PasswordTooShort,
    PasswordTooLong,
    CountryMissing,
    CountryBlocked,
    DocumentsNotAccepted,

    /// <summary>A document changed after the form was shown, so what was accepted isn't current.</summary>
    DocumentsChanged,
}

public enum SignInOutcome
{
    /// <summary><see cref="SignInResult.Principal"/> is the principal to sign in.</summary>
    SignedIn,

    /// <summary>The password was right and two-factor is on. <see cref="SignInResult.Challenge"/> goes to the second step.</summary>
    CodeRequired,

    Refused,

    /// <summary>The challenge expired, was used, or something ended it, so the user signs in again.</summary>
    ChallengeExpired,
}

/// <summary>
/// How a sign-in step went. Not a record, whose ToString would print the challenge, which is as good as
/// the password until it expires.
/// </summary>
public sealed class SignInResult
{
    private SignInResult(SignInOutcome outcome) => Outcome = outcome;

    public static SignInResult Refused { get; } = new(SignInOutcome.Refused);

    public static SignInResult ChallengeExpired { get; } = new(SignInOutcome.ChallengeExpired);

    public SignInOutcome Outcome { get; }

    public ClaimsPrincipal? Principal { get; private init; }

    /// <summary>The token for the second step. Keep it in a cookie only that step's page can read, never in a URL.</summary>
    public string? Challenge { get; private init; }

    public DateTimeOffset? ChallengeExpiresAt { get; private init; }

    public static SignInResult SignedIn(ClaimsPrincipal principal) => new(SignInOutcome.SignedIn) { Principal = principal };

    public static SignInResult CodeRequired(string challenge, DateTimeOffset expiresAt) =>
        new(SignInOutcome.CodeRequired) { Challenge = challenge, ChallengeExpiresAt = expiresAt };

    public override string ToString() => Outcome.ToString();
}

public enum PasswordChangeResult
{
    Changed,
    CurrentPasswordWrong,
    PasswordTooShort,
    PasswordTooLong,
}

/// <param name="EmailVerified">
/// Whether a link or the bootstrap secret has proved the address. With email off this stays false for
/// most users, and nothing needs it to be true (EXT-EML-01).
/// </param>
public sealed record AccountOverview(string Email, bool EmailVerified, IReadOnlyList<LegalAcceptanceRecord> Acceptances);

public sealed record LegalAcceptanceRecord(LegalDocument Document, string Version, DateTimeOffset AcceptedAt);
