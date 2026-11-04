using System.Security.Claims;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Logs user IDs and never addresses, which FR-LOG-05 doesn't mask. A refusal logs at Information,
/// because at Warning anonymous visitors could write a database row per attempt. The audit trail
/// records every sign-in attempt anyway (FR-AUD-01), which the sign-in rate limit bounds.
/// </summary>
internal sealed class Accounts(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IPasswordHasher hasher,
    PasswordChecks passwords,
    Sessions sessions,
    TwoFactor twoFactor,
    EmailVerification verification,
    IAuditTrail audit,
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

        IssuedToken? verificationLink = null;

        if (created > 0)
        {
            await RecordAcceptanceAsync(context, userId, now, cancellationToken);

            if (settings.EmailEnabled)
            {
                verificationLink = await verification.IssueAsync(context, userId, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        // In the background, so a taken address, which sends nothing, takes as long to answer.
        if (verificationLink is not null)
        {
            using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

            verification.Send(email, verificationLink);
        }

        return [];
    }

    public async Task<SignInResult> SignInAsync(string email, string password, ClientDetails client, CancellationToken cancellationToken)
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

        var hasTwoFactor = await TwoFactor.IsEnabledAsync(context, user.Id, cancellationToken);

        switch (await passwords.CheckAsync(context, user.Id, password, user.PasswordHash, clearFailures: !hasTwoFactor, cancellationToken))
        {
            case PasswordCheck.Locked:
                logger.LogInformation("Sign-in refused for a locked account");
                return await RefuseAsync(context, user.Id, cancellationToken);

            case PasswordCheck.Wrong:
                logger.LogInformation("Sign-in refused for a wrong password");
                return await RefuseAsync(context, user.Id, cancellationToken);
        }

        if (hasTwoFactor)
        {
            // No session and no SignedIn record yet: the password is only half of it.
            if (await SignInChallenges.IssueAsync(context, user.Id, user.PasswordHash, cancellationToken) is not { } challenge)
            {
                logger.LogInformation("Sign-in refused because the password changed while it ran");
                return await RefuseAsync(context, user.Id, cancellationToken);
            }

            logger.LogInformation("Password accepted, and a two-factor code is needed");

            return SignInResult.CodeRequired(challenge.Token, challenge.ExpiresAt);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var started = await sessions.StartAsync(context, user.Id, user.PasswordHash, client, cancellationToken);

        audit.Add(
            context,
            started is { } newSession
                ? AuditEntry.ByUser(AuditAction.SignedIn, user.Id) with { TargetType = Sessions.AuditTarget, TargetId = newSession.ToString() }
                : SignInFailed(user.Id));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (started is not { } sessionId)
        {
            logger.LogInformation("Sign-in refused because the password changed while it ran");

            return SignInResult.Refused;
        }

        logger.LogInformation("Signed in");

        return SignInResult.SignedIn(Principal(user.Id, sessionId, await HasAcceptedCurrentAsync(context, user.Id, cancellationToken)));
    }

    public async Task<SignInResult> CompleteSignInAsync(string? challenge, string code, ClientDetails client, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (await SignInChallenges.FindAsync(context, challenge, cancellationToken) is not { } userId)
        {
            return SignInResult.ChallengeExpired;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        // A guess at the code costs what a guess at the password does, from the same budget.
        if (!await passwords.TryChargeAttemptAsync(context, userId, cancellationToken))
        {
            logger.LogInformation("Sign-in refused for a locked account at the two-factor step");
            return await RefuseAsync(context, userId, cancellationToken);
        }

        if (await twoFactor.MatchAsync(context, userId, TwoFactorCode.Parse(code), cancellationToken) is not { } match)
        {
            logger.LogInformation("Sign-in refused for a wrong two-factor code");
            return await RefuseAsync(context, userId, cancellationToken);
        }

        // Both factors were right, as a right password clears the count. Before the transaction, which
        // holds the users row FOR SHARE: updating it in there would deadlock with a second submission.
        await PasswordChecks.ClearFailuresAsync(context, userId, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The users row is locked first, by starting the session, as a password change locks it first.
        // So the two queue rather than deadlock, and a change that commits first leaves no hash to match.
        var hash = await context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => candidate.PasswordHash)
            .SingleOrDefaultAsync(cancellationToken);

        if (hash is null || await sessions.StartAsync(context, userId, hash, client, cancellationToken) is not { } sessionId)
        {
            logger.LogInformation("Sign-in refused because the password changed at the two-factor step");
            return SignInResult.Refused;
        }

        if (!await SignInChallenges.UseAsync(context, challenge, userId, cancellationToken))
        {
            logger.LogInformation("Sign-in refused because the challenge ended while the code was checked");
            return SignInResult.ChallengeExpired;
        }

        if (!await twoFactor.SpendAsync(context, userId, match, cancellationToken))
        {
            logger.LogInformation("Sign-in refused because the code was used while it was checked");
            return SignInResult.Refused;
        }

        audit.Add(context, AuditEntry.ByUser(AuditAction.SignedIn, userId) with { TargetType = Sessions.AuditTarget, TargetId = sessionId.ToString() });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Signed in with two-factor");

        return SignInResult.SignedIn(Principal(userId, sessionId, await HasAcceptedCurrentAsync(context, userId, cancellationToken)));
    }

    public async Task<bool> IsChallengeLiveAsync(string? challenge, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await SignInChallenges.FindAsync(context, challenge, cancellationToken) is not null;
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

        if (await passwords.CheckAsync(context, userId, currentPassword, verifiedHash, clearFailures: true, cancellationToken) != PasswordCheck.Right)
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

        // A link asked for before the change mustn't outlive it, and nor must a sign-in waiting for its code.
        await AccountTokens.DeleteAsync(context, userId, AccountTokenPurpose.PasswordReset, cancellationToken);
        await SignInChallenges.DeleteAsync(context, userId, cancellationToken);

        audit.Add(context, AuditEntry.ByUser(AuditAction.PasswordChanged, userId));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        sessions.EndConnections(revoked);
        logger.LogInformation("Password changed");

        return PasswordChangeResult.Changed;
    }

    public async Task<AccountOverview?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var account = await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Email, user.EmailVerifiedAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            return null;
        }

        var acceptances = await context.Set<LegalAcceptance>()
            .Where(acceptance => acceptance.UserId == userId)
            .OrderByDescending(acceptance => acceptance.AcceptedAt)
            .Select(acceptance => new LegalAcceptanceRecord(acceptance.Document, acceptance.Version, acceptance.AcceptedAt))
            .ToListAsync(cancellationToken);

        return new AccountOverview(account.Email, account.EmailVerifiedAt is not null, acceptances);
    }

    /// <summary>
    /// Recorded with no account, and without the address, which might be a password typed into the
    /// wrong field. Writing a row here also brings this closer to what a wrong password costs.
    /// </summary>
    private async Task<SignInResult> RefuseUnknownAddressAsync(string password, CancellationToken cancellationToken)
    {
        await hasher.SpendAsync(password, cancellationToken);
        logger.LogInformation("Sign-in refused for an unknown address");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await RefuseAsync(context, userId: null, cancellationToken);
    }

    /// <summary>
    /// Saved on its own, after the outcome is known, because the attempt was charged before the hash
    /// ran, and that has to commit first. The reason is in the log, under the same correlation ID.
    /// </summary>
    private async Task<SignInResult> RefuseAsync(EcnanibDbContext context, Guid? userId, CancellationToken cancellationToken)
    {
        audit.Add(context, SignInFailed(userId));
        await context.SaveChangesAsync(cancellationToken);

        return SignInResult.Refused;
    }

    /// <summary>By a visitor, because a failed attempt proves nothing about who sent it.</summary>
    private static AuditEntry SignInFailed(Guid? userId) =>
        new() { Action = AuditAction.SignInFailed, ActorKind = AuditActorKind.Visitor, UserId = userId };

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
}
