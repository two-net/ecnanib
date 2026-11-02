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
    /// afterwards can still tell: see Registration in docs/architecture.md.
    /// </summary>
    Task<IReadOnlyList<RegistrationProblem>> RegisterAsync(Registration registration, CancellationToken cancellationToken);

    /// <summary>
    /// The principal to sign in, or null. An unknown address, a wrong password and a locked account
    /// are one result, and each spends one hash, so tell the user no more than that.
    /// </summary>
    Task<ClaimsPrincipal?> SignInAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Records acceptance of the versions the user was shown. Null, recording nothing, unless both are
    /// current. Otherwise the principal to sign in, which replaces the one in the cookie.
    /// </summary>
    Task<ClaimsPrincipal?> AcceptLegalDocumentsAsync(
        Guid userId,
        string? termsOfUseVersion,
        string? riskDisclosureVersion,
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

public sealed record AccountOverview(string Email, IReadOnlyList<LegalAcceptanceRecord> Acceptances);

public sealed record LegalAcceptanceRecord(LegalDocument Document, string Version, DateTimeOffset AcceptedAt);
