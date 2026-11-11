using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>Registers accounts and verifies their email addresses. See docs/accounts.md.</summary>
/// <remarks>
/// <para>
/// A registration never changes an account that exists. If it replaced the password of an
/// account that is not verified yet, anyone who knows the address could take that account over
/// while its owner is signed in to it.
/// </para>
/// <para>
/// Every registration does the same work whatever the address is: it hashes the password, writes
/// to the database, and sends one email. So neither the answer nor the time it takes tells
/// whether an address has an account.
/// </para>
/// </remarks>
internal sealed class Registration(
    ApplicationSettings application,
    IDbContextFactory<EcnanibDbContext> contexts,
    PasswordHasher passwords,
    IAccountMailer mailer,
    TimeProvider clock,
    ILogger<Registration> logger) : IRegistration
{
    // The longest address the mail protocol can carry.
    private const int LongestEmail = 254;

    /// <summary>What the address of a registration turned out to be.</summary>
    private enum Address
    {
        New,
        RegisteredNotVerified,
        RegisteredAndVerified,
    }

    public bool IsOpen => application.RegistrationOpen;

    public async Task<RegistrationResult> RegisterAsync(
        RegistrationRequest request, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(password);

        if (!IsOpen)
        {
            return RegistrationResult.Of(RegistrationOutcome.Closed);
        }

        var email = Normalize(request.Email);
        var problems = ProblemsOf(email, password, request.Accepted);
        if (problems.Count > 0)
        {
            return RegistrationResult.Invalid(problems);
        }

        // Before the address is looked up, so that the time it takes is spent either way.
        var passwordHash = await passwords.HashAsync(password);
        var (token, tokenHash) = VerificationToken.Create();

        var address = await StoreAsync(email, passwordHash, tokenHash, cancellationToken);

        // After the transaction has ended: a link must not arrive before the account it is for.
        try
        {
            if (address == Address.RegisteredAndVerified)
            {
                await mailer.SendAlreadyRegisteredNoticeAsync(email, cancellationToken);
            }
            else
            {
                await mailer.SendVerificationLinkAsync(
                    email,
                    EmailVerification.LinkFor(application.PublicUrl, token),
                    registeredBefore: address == Address.RegisteredNotVerified,
                    cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The account stays. Registering again sends a new link.
            logger.LogError(error, "The email of a registration could not be sent");

            return RegistrationResult.Of(RegistrationOutcome.EmailNotSent);
        }

        return RegistrationResult.Of(RegistrationOutcome.Accepted);
    }

    public async Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (VerificationToken.HashOf(token) is not { } tokenHash)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var userId = await context.Set<User>()
            .Where(user => user.EmailVerificationTokenHash == tokenHash)
            .Select(user => (Guid?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return false;
        }

        // One statement, and only while the link still stands: of two requests with the same
        // link, one verifies the address and the other finds the link used.
        var verified = await context.Set<User>()
            .Where(user => user.Id == userId
                && user.EmailVerificationTokenHash == tokenHash
                && user.EmailVerificationExpiresAt > now
                && user.EmailVerifiedAt == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(user => user.EmailVerifiedAt, (DateTimeOffset?)now)
                    .SetProperty(user => user.EmailVerificationTokenHash, (byte[]?)null)
                    .SetProperty(user => user.EmailVerificationExpiresAt, (DateTimeOffset?)null),
                cancellationToken);

        if (verified == 1)
        {
            logger.LogInformation("The email address of user {UserId} was verified", userId);
        }

        return verified == 1;
    }

    /// <summary>
    /// Creates the account when the address has none. When it has one that is not verified, only
    /// its verification link is replaced. A verified account is not touched.
    /// </summary>
    private async Task<Address> StoreAsync(
        string email, string passwordHash, byte[] tokenHash, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        var expiresAt = now + EmailVerification.LinkLifetime;

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Whoever inserts the row has the address. Of two requests for one address at the same
        // time, the second waits here, and then finds the account of the first.
        var inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO users (id, email, password_hash, registered_at, email_verification_token_hash, email_verification_expires_at)
            VALUES ({id}, {email}, {passwordHash}, {now}, {tokenHash}, {expiresAt})
            ON CONFLICT (email) DO NOTHING
            """,
            cancellationToken);

        Address address;
        if (inserted == 1)
        {
            // The versions in force are the ones the request accepted: it was refused otherwise.
            context.Set<LegalAcceptance>().AddRange(LegalDocuments.All.Select(document =>
                new LegalAcceptance(id, document, LegalDocuments.CurrentVersionOf(document), now)));
            await context.SaveChangesAsync(cancellationToken);

            address = Address.New;
        }
        else
        {
            var relinked = await context.Set<User>()
                .Where(user => user.Email == email && user.EmailVerifiedAt == null)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(user => user.EmailVerificationTokenHash, (byte[]?)tokenHash)
                        .SetProperty(user => user.EmailVerificationExpiresAt, (DateTimeOffset?)expiresAt),
                    cancellationToken);

            address = relinked == 1 ? Address.RegisteredNotVerified : Address.RegisteredAndVerified;
        }

        await transaction.CommitAsync(cancellationToken);

        // The address is personal data, so the log names the account and not the address.
        switch (address)
        {
            case Address.New:
                logger.LogInformation("User {UserId} registered", id);
                break;

            case Address.RegisteredNotVerified:
                logger.LogInformation(
                    "A registration named an address that is registered and not verified. Its verification link was replaced");
                break;

            default:
                logger.LogInformation(
                    "A registration named an address that has a verified account. The account was not changed");
                break;
        }

        return address;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private static List<RegistrationProblem> ProblemsOf(
        string email, string password, IReadOnlyCollection<AcceptedDocument> accepted)
    {
        var problems = new List<RegistrationProblem>();

        if (!LooksLikeEmailAddress(email))
        {
            problems.Add(RegistrationProblem.EmailInvalid);
        }

        // In characters as a person counts them, not in the units the text is stored in.
        var length = password.EnumerateRunes().Count();
        if (length < PasswordRules.MinimumLength)
        {
            problems.Add(RegistrationProblem.PasswordTooShort);
        }
        else if (length > PasswordRules.MaximumLength)
        {
            problems.Add(RegistrationProblem.PasswordTooLong);
        }

        foreach (var document in LegalDocuments.All)
        {
            // In the version in force. A form that showed an earlier one is asked to show this one.
            var current = new AcceptedDocument(document, LegalDocuments.CurrentVersionOf(document));
            if (!accepted.Contains(current))
            {
                problems.Add(document switch
                {
                    LegalDocument.TermsOfService => RegistrationProblem.TermsOfServiceNotAccepted,
                    LegalDocument.PrivacyPolicy => RegistrationProblem.PrivacyPolicyNotAccepted,
                    LegalDocument.RiskDisclosure => RegistrationProblem.RiskDisclosureNotAccepted,
                    _ => throw new ArgumentOutOfRangeException(nameof(accepted), document, "The document has no problem to report."),
                });
            }
        }

        return problems;
    }

    /// <summary>
    /// One <c>@</c> with something before it and a domain with a dot after it, and no spaces or
    /// control characters. Whether the address exists is what verifying it finds out.
    /// </summary>
    private static bool LooksLikeEmailAddress(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || email.Length > LongestEmail)
        {
            return false;
        }

        var domain = email[(at + 1)..];

        return domain.Contains('.')
            && domain.Split('.').All(label => label.Length > 0)
            && !email.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    }
}
