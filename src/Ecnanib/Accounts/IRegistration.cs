namespace Ecnanib.Accounts;

/// <summary>
/// Registration: a visitor creates an account with an email address and a password, accepts the
/// legal documents, and verifies the address (FR-ACC-1, FR-ACC-8). See docs/accounts.md.
/// </summary>
public interface IRegistration
{
    /// <summary>Whether new accounts are accepted, as the configuration file says (FR-ACC-7).</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Registers an account and sends the link that verifies its address. An address that already
    /// has an account gets the same answer as a new one, so the answer does not tell whether an
    /// address is registered. The existing account is left as it is, and its owner is sent an email.
    /// </summary>
    /// <param name="request">The address, and the documents the visitor accepted.</param>
    /// <param name="password">
    /// The password. It is passed on its own, not inside the request, so that it is never part of
    /// a value that can be printed or logged.
    /// </param>
    /// <param name="cancellationToken">Cancels the registration.</param>
    Task<RegistrationResult> RegisterAsync(RegistrationRequest request, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the address of the account that the token of a verification link belongs to.
    /// </summary>
    /// <param name="token">The token of the link.</param>
    /// <param name="newPassword">
    /// The password that whoever confirms the link chooses, or null when they gave none. Only
    /// the link of an address that was registered again needs one: the password stored with the
    /// account may be someone else's, so the person who reads the mail of the address chooses it.
    /// The sessions of the account end with the earlier password. A first link takes no password.
    /// </param>
    /// <param name="origin">
    /// Where the request came from, for the audit trail, which records a password that is chosen
    /// this way.
    /// </param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    Task<EmailVerificationOutcome> VerifyEmailAsync(
        string token, string? newPassword, AuditOrigin origin, CancellationToken cancellationToken);
}

/// <summary>How confirming a verification link ended.</summary>
public enum EmailVerificationOutcome
{
    /// <summary>The address is verified.</summary>
    Verified,

    /// <summary>The token is not one, was used, was replaced by a later link, or has expired.</summary>
    LinkNotValid,

    /// <summary>The link is of an address that was registered again, and no password came with it. Nothing was done.</summary>
    PasswordNeeded,

    PasswordTooShort,
    PasswordTooLong,
}

/// <summary>What a visitor fills in on the registration form, except the password.</summary>
/// <param name="Email">The email address, as typed.</param>
/// <param name="Accepted">The documents the visitor accepted, each in the version the form showed.</param>
public sealed record RegistrationRequest(string Email, IReadOnlyCollection<AcceptedDocument> Accepted);

/// <summary>How a registration ended.</summary>
public enum RegistrationOutcome
{
    /// <summary>The request was taken, and an email is on its way to the address.</summary>
    Accepted,

    /// <summary>Registration is closed, so nothing was done.</summary>
    Closed,

    /// <summary>The request breaks a rule, so nothing was done. The problems say which.</summary>
    Invalid,

    /// <summary>The email could not be sent. Registering again sends another.</summary>
    EmailNotSent,
}

/// <summary>A rule that a registration request breaks.</summary>
public enum RegistrationProblem
{
    EmailInvalid,
    PasswordTooShort,
    PasswordTooLong,

    /// <summary>The Terms of Service were not accepted in the version that is in force.</summary>
    TermsOfServiceNotAccepted,

    /// <summary>The Privacy Policy was not accepted in the version that is in force.</summary>
    PrivacyPolicyNotAccepted,

    /// <summary>The risk disclosure was not accepted in the version that is in force.</summary>
    RiskDisclosureNotAccepted,
}

/// <summary>The outcome of a registration, with the problems of a request that was invalid.</summary>
public sealed record RegistrationResult(RegistrationOutcome Outcome, IReadOnlyList<RegistrationProblem> Problems)
{
    internal static RegistrationResult Of(RegistrationOutcome outcome) => new(outcome, []);

    internal static RegistrationResult Invalid(IReadOnlyList<RegistrationProblem> problems) =>
        new(RegistrationOutcome.Invalid, problems);
}
