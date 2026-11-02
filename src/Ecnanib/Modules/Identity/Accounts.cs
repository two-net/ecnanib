using System.Security.Claims;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Logs user IDs and never addresses, which FR-LOG-05 doesn't mask. A refusal logs at Information,
/// because at Warning anonymous visitors could write a database row per attempt.
/// </summary>
internal sealed class Accounts(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IPasswordHasher hasher,
    Sessions sessions,
    IdentitySettings settings,
    ILogger<Accounts> logger) : IAccounts
{
    public RegistrationRules RegistrationRules { get; } = new(
        settings.PasswordMinLength,
        IdentitySettings.PasswordMaxLength,
        settings.BlockedCountries.Count > 0);

    public async Task<IReadOnlyList<RegistrationProblem>> RegisterAsync(Registration registration, CancellationToken cancellationToken)
    {
        var problems = FindProblems(registration);

        if (problems.Count > 0)
        {
            return problems;
        }

        // Hashed even when the address is taken, or the time taken would answer what the result hides.
        var hash = await hasher.HashAsync(registration.Password, cancellationToken);
        var userId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var email = registration.Email.Trim();
        var normalizedEmail = EmailAddresses.Normalize(email);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Not SaveChanges and a caught exception: EF logs a failed save as an Error, so a taken
        // address would write a database row for anyone who asks. This also settles the race.
        var created = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO users (id, email, normalized_email, password_hash, failed_sign_ins, created_at)
            VALUES ({userId}, {email}, {normalizedEmail}, {hash}, 0, {now})
            ON CONFLICT (normalized_email) DO NOTHING
            """,
            cancellationToken);

        if (created > 0)
        {
            await RecordAcceptanceAsync(context, userId, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return [];
    }

    public async Task<ClaimsPrincipal?> SignInAsync(string email, string password, ClientDetails client, CancellationToken cancellationToken)
    {
        if (!EmailAddresses.IsValid(email))
        {
            return await RefuseUnknownAddressAsync(password, cancellationToken);
        }

        var normalizedEmail = EmailAddresses.Normalize(email);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await context.Set<User>()
            .Where(candidate => candidate.NormalizedEmail == normalizedEmail)
            .Select(candidate => new { candidate.Id, candidate.PasswordHash })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return await RefuseUnknownAddressAsync(password, cancellationToken);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = user.Id });

        switch (await CheckPasswordAsync(context, user.Id, password, user.PasswordHash, cancellationToken))
        {
            case PasswordCheck.Locked:
                logger.LogInformation("Sign-in refused for a locked account");
                return null;

            case PasswordCheck.Wrong:
                logger.LogInformation("Sign-in refused for a wrong password");
                return null;
        }

        if (await sessions.StartAsync(user.Id, user.PasswordHash, client, cancellationToken) is not { } sessionId)
        {
            logger.LogInformation("Sign-in refused because the password changed while it ran");

            return null;
        }

        logger.LogInformation("Signed in");

        return Principal(user.Id, sessionId, await HasAcceptedCurrentAsync(context, user.Id, cancellationToken));
    }

    public async Task<ClaimsPrincipal?> AcceptLegalDocumentsAsync(
        ClaimsPrincipal user,
        string? termsOfUseVersion,
        string? riskDisclosureVersion,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.UserId(user) is not { } userId
            || AccountClaims.SessionId(user) is not { } sessionId
            || termsOfUseVersion != LegalDocuments.TermsOfUseVersion
            || riskDisclosureVersion != LegalDocuments.RiskDisclosureVersion)
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        await RecordAcceptanceAsync(context, userId, DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Principal(userId, sessionId, legalDocumentsAccepted: true);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(
        ClaimsPrincipal user,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (newPassword.Length < settings.PasswordMinLength)
        {
            return PasswordChangeResult.PasswordTooShort;
        }

        if (newPassword.Length > IdentitySettings.PasswordMaxLength)
        {
            return PasswordChangeResult.PasswordTooLong;
        }

        if (AccountClaims.UserId(user) is not { } userId || AccountClaims.SessionId(user) is not { } sessionId)
        {
            return PasswordChangeResult.CurrentPasswordWrong;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var verifiedHash = await context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => candidate.PasswordHash)
            .SingleOrDefaultAsync(cancellationToken);

        if (verifiedHash is null)
        {
            return PasswordChangeResult.CurrentPasswordWrong;
        }

        if (await CheckPasswordAsync(context, userId, currentPassword, verifiedHash, cancellationToken) != PasswordCheck.Right)
        {
            logger.LogInformation("Password change refused");

            return PasswordChangeResult.CurrentPasswordWrong;
        }

        // Before the transaction, because a hash this slow mustn't hold one open.
        var newHash = await hasher.HashAsync(newPassword, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The hash first: a sign-in waits on this row, so a session it starts can't miss the delete below.
        var changed = await context.Set<User>()
            .Where(candidate => candidate.Id == userId && candidate.PasswordHash == verifiedHash)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.PasswordHash, newHash), cancellationToken);

        if (changed == 0)
        {
            logger.LogInformation("Password change refused because another finished first");

            return PasswordChangeResult.CurrentPasswordWrong;
        }

        var revoked = await sessions.DeleteOthersAsync(context, userId, sessionId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        sessions.EndConnections(revoked);
        logger.LogInformation("Password changed");

        return PasswordChangeResult.Changed;
    }

    public async Task<AccountOverview?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var email = await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (email is null)
        {
            return null;
        }

        var acceptances = await context.Set<LegalAcceptance>()
            .Where(acceptance => acceptance.UserId == userId)
            .OrderByDescending(acceptance => acceptance.AcceptedAt)
            .Select(acceptance => new LegalAcceptanceRecord(acceptance.Document, acceptance.Version, acceptance.AcceptedAt))
            .ToListAsync(cancellationToken);

        return new AccountOverview(email, acceptances);
    }

    private async Task<ClaimsPrincipal?> RefuseUnknownAddressAsync(string password, CancellationToken cancellationToken)
    {
        await hasher.SpendAsync(password, cancellationToken);
        logger.LogInformation("Sign-in refused for an unknown address");

        return null;
    }

    /// <summary>
    /// Everything that guards a password, for sign-in and for changing it. A locked account spends a
    /// hash, so it takes as long as a wrong password. A right one clears the count.
    /// </summary>
    private async Task<PasswordCheck> CheckPasswordAsync(
        EcnanibDbContext context,
        Guid userId,
        string password,
        string hash,
        CancellationToken cancellationToken)
    {
        if (!await TryChargeAttemptAsync(context, userId, cancellationToken))
        {
            await hasher.SpendAsync(password, cancellationToken);

            return PasswordCheck.Locked;
        }

        if (!await hasher.VerifyAsync(password, hash, cancellationToken))
        {
            return PasswordCheck.Wrong;
        }

        await context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.FailedSignIns, 0)
                    .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null),
                cancellationToken);

        return PasswordCheck.Right;
    }

    /// <summary>
    /// Charged before the hash runs and in one statement, so parallel guesses can't all pass the check
    /// during a slow hash, and a lock window allows MaxFailedSignIns verifications however they arrive.
    /// A row lock queues concurrent attempts, and each then sees the last one's result. False when the
    /// account is locked. Attempts during a lock change nothing, but someone can lock an account again
    /// as soon as a lock ends: see Lockout in docs/architecture.md.
    /// </summary>
    private async Task<bool> TryChargeAttemptAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lockedUntil = now + settings.Lockout;
        var limit = settings.MaxFailedSignIns;

        var charged = await context.Set<User>()
            .Where(user => user.Id == userId && (user.LockedUntil == null || user.LockedUntil <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.FailedSignIns, user => user.FailedSignIns + 1 >= limit ? 0 : user.FailedSignIns + 1)
                    .SetProperty(user => user.LockedUntil, user => user.FailedSignIns + 1 >= limit ? lockedUntil : (DateTimeOffset?)null),
                cancellationToken);

        return charged > 0;
    }

    private List<RegistrationProblem> FindProblems(Registration registration)
    {
        var problems = new List<RegistrationProblem>();

        if (!EmailAddresses.IsValid(registration.Email))
        {
            problems.Add(RegistrationProblem.InvalidEmail);
        }

        if (registration.Password.Length < settings.PasswordMinLength)
        {
            problems.Add(RegistrationProblem.PasswordTooShort);
        }

        if (registration.Password.Length > IdentitySettings.PasswordMaxLength)
        {
            problems.Add(RegistrationProblem.PasswordTooLong);
        }

        if (settings.BlockedCountries.Count > 0)
        {
            if (registration.Country is not { } country || !Countries.IsKnown(country))
            {
                problems.Add(RegistrationProblem.CountryMissing);
            }
            else if (settings.BlockedCountries.Contains(country))
            {
                problems.Add(RegistrationProblem.CountryBlocked);
            }
        }

        if (registration.TermsOfUseVersion is null || registration.RiskDisclosureVersion is null)
        {
            problems.Add(RegistrationProblem.DocumentsNotAccepted);
        }
        else if (registration.TermsOfUseVersion != LegalDocuments.TermsOfUseVersion
            || registration.RiskDisclosureVersion != LegalDocuments.RiskDisclosureVersion)
        {
            problems.Add(RegistrationProblem.DocumentsChanged);
        }

        return problems;
    }

    /// <summary>Takes the caller's context, so that a new account and its acceptance commit together.</summary>
    private static async Task RecordAcceptanceAsync(
        EcnanibDbContext context,
        Guid userId,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        (LegalDocument Document, string Version)[] documents =
        [
            (LegalDocument.TermsOfUse, LegalDocuments.TermsOfUseVersion),
            (LegalDocument.RiskDisclosure, LegalDocuments.RiskDisclosureVersion),
        ];

        foreach (var (document, version) in documents)
        {
            // Accepting again from a second device keeps the first timestamp.
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO legal_acceptances (user_id, document, version, accepted_at)
                VALUES ({userId}, {document.ToString()}, {version}, {acceptedAt})
                ON CONFLICT DO NOTHING
                """,
                cancellationToken);
        }
    }

    private static async Task<bool> HasAcceptedCurrentAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var accepted = await context.Set<LegalAcceptance>()
            .CountAsync(
                acceptance => acceptance.UserId == userId
                    && ((acceptance.Document == LegalDocument.TermsOfUse && acceptance.Version == LegalDocuments.TermsOfUseVersion)
                        || (acceptance.Document == LegalDocument.RiskDisclosure
                            && acceptance.Version == LegalDocuments.RiskDisclosureVersion)),
                cancellationToken);

        return accepted == 2;
    }

    private static ClaimsPrincipal Principal(Guid userId, Guid sessionId, bool legalDocumentsAccepted)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(AccountClaims.Session, sessionId.ToString()),
        };

        if (legalDocumentsAccepted)
        {
            claims.Add(new Claim(AccountClaims.LegalDocuments, LegalDocuments.Current));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private enum PasswordCheck
    {
        Right,
        Wrong,
        Locked,
    }
}
