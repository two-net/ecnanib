using System.Security.Claims;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// FR-AUTH-05. The key is a secret in the secret store, and only this class reveals it, to check a code.
/// Logs user IDs, and never a key or a code. See Two-factor in docs/architecture.md.
/// </summary>
internal sealed class TwoFactor(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ISecretStore secrets,
    PasswordChecks passwords,
    Sessions sessions,
    IAuditTrail audit,
    IdentitySettings settings,
    TimeProvider time,
    ILogger<TwoFactor> logger) : ITwoFactor
{
    /// <summary>What an authenticator app shows the account under. The product's name, which never changes with the language.</summary>
    private const string Issuer = "Ecnanib";

    public async Task<TwoFactorStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var authenticator = await context.Set<TotpAuthenticator>()
            .Where(row => row.UserId == userId)
            .Select(row => new { row.EnabledAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (authenticator is null)
        {
            return new TwoFactorStatus(TwoFactorState.Off, 0);
        }

        if (authenticator.EnabledAt is null)
        {
            return new TwoFactorStatus(TwoFactorState.SetupPending, 0);
        }

        var left = await context.Set<RecoveryCode>().CountAsync(code => code.UserId == userId, cancellationToken);

        return new TwoFactorStatus(TwoFactorState.On, left);
    }

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await IsEnabledAsync(context, userId, cancellationToken);
    }

    public async Task<TotpSetup?> BeginSetupAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Holds two setups at once apart, so neither leaves a key behind that nothing refers to.
        var email = await context.Database
            .SqlQuery<string>($"""SELECT email AS "Value" FROM users WHERE id = {userId} FOR NO KEY UPDATE""")
            .SingleOrDefaultAsync(cancellationToken);

        if (email is null)
        {
            return null;
        }

        var existing = await context.Set<TotpAuthenticator>()
            .Where(row => row.UserId == userId)
            .Select(row => new { row.SecretId, row.EnabledAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing?.EnabledAt is not null)
        {
            return null;
        }

        using var key = Totp.NewKey();
        var secretId = secrets.Add(context, key);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            context.Set<TotpAuthenticator>().Add(new TotpAuthenticator { UserId = userId, SecretId = secretId, CreatedAt = now });
        }
        else
        {
            await context.Set<TotpAuthenticator>()
                .Where(row => row.UserId == userId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.SecretId, secretId)
                        .SetProperty(row => row.CreatedAt, now),
                    cancellationToken);

            await secrets.RemoveAsync(context, existing.SecretId, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Started setting up two-factor");

        return Setup(email, key);
    }

    public async Task<TotpSetup?> FindSetupAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = await context.Set<TotpAuthenticator>()
            .Where(row => row.UserId == userId && row.EnabledAt == null)
            .Join(context.Set<User>(), row => row.UserId, user => user.Id, (row, user) => new { row.SecretId, user.Email })
            .SingleOrDefaultAsync(cancellationToken);

        if (pending is null)
        {
            return null;
        }

        using var key = await secrets.RevealAsync(pending.SecretId, cancellationToken);

        return Setup(pending.Email, key);
    }

    public async Task<EnableTwoFactorResult> EnableAsync(ClaimsPrincipal user, string password, string code, CancellationToken cancellationToken)
    {
        if (AccountClaims.UserId(user) is not { } userId || AccountClaims.SessionId(user) is not { } sessionId)
        {
            return new EnableTwoFactorResult(EnableTwoFactorOutcome.PasswordWrong);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var hash = await context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => candidate.PasswordHash)
            .SingleOrDefaultAsync(cancellationToken);

        var pending = await context.Set<TotpAuthenticator>()
            .Where(row => row.UserId == userId && row.EnabledAt == null)
            .Select(row => new { row.SecretId, row.LastUsedStep })
            .SingleOrDefaultAsync(cancellationToken);

        if (hash is null || pending is null)
        {
            return new EnableTwoFactorResult(EnableTwoFactorOutcome.NoSetup);
        }

        if (await passwords.CheckAsync(context, userId, password, hash, clearFailures: true, cancellationToken) != PasswordCheck.Right)
        {
            logger.LogInformation("Turning two-factor on was refused for a wrong password");

            return new EnableTwoFactorResult(EnableTwoFactorOutcome.PasswordWrong);
        }

        var parsed = TwoFactorCode.Parse(code);
        long? step = null;

        if (parsed.Kind == TwoFactorCodeKind.Totp)
        {
            using var key = await secrets.RevealAsync(pending.SecretId, cancellationToken);
            step = Totp.Match(key, parsed.Value, Totp.StepAt(time.GetUtcNow()), pending.LastUsedStep);
        }

        if (step is not { } matched)
        {
            logger.LogInformation("Turning two-factor on was refused for a wrong code");

            return new EnableTwoFactorResult(EnableTwoFactorOutcome.CodeWrong);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Conditional on the key that was checked, so a setup started again meanwhile isn't confirmed by a code from the old one.
        var enabled = await context.Set<TotpAuthenticator>()
            .Where(row => row.UserId == userId && row.SecretId == pending.SecretId && row.EnabledAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.EnabledAt, DateTimeOffset.UtcNow)
                    .SetProperty(row => row.LastUsedStep, matched)
                    .SetProperty(row => row.FailedChecks, 0)
                    .SetProperty(row => row.ChecksLockedUntil, (DateTimeOffset?)null),
                cancellationToken);

        if (enabled == 0)
        {
            return new EnableTwoFactorResult(EnableTwoFactorOutcome.NoSetup);
        }

        var codes = RecoveryCodes.New();
        await RecoveryCodes.ReplaceAsync(context, userId, codes, cancellationToken);

        // None of them passed two-factor, which is now required at sign-in.
        var ended = await sessions.DeleteOthersAsync(context, userId, sessionId, cancellationToken);

        audit.Add(context, AuditEntry.ByUser(AuditAction.TwoFactorEnabled, userId));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        sessions.EndConnections(ended);
        logger.LogInformation("Turned two-factor on, and ended {SessionCount} other sessions", ended.Count);

        return new EnableTwoFactorResult(EnableTwoFactorOutcome.Enabled, new RecoveryCodeSet(codes));
    }

    public async Task<TwoFactorCheck> CheckAsync(Guid userId, string? code, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await IsEnabledAsync(context, userId, cancellationToken))
        {
            return TwoFactorCheck.NotRequired;
        }

        var parsed = TwoFactorCode.Parse(code);

        if (parsed.Kind == TwoFactorCodeKind.None)
        {
            logger.LogInformation("A two-factor check was refused for something that isn't a code");

            return TwoFactorCheck.Refused;
        }

        // Recovery codes aren't charged, so they still work during a lock: 50 random bits can't be guessed.
        if (parsed.Kind == TwoFactorCodeKind.Totp && !await TryChargeCheckAsync(context, userId, cancellationToken))
        {
            logger.LogInformation("A two-factor check was refused, because wrong codes have paused them");

            return TwoFactorCheck.Throttled;
        }

        if (await MatchAsync(context, userId, parsed, cancellationToken) is not { } match)
        {
            logger.LogInformation("A two-factor check was refused for a wrong code");

            return TwoFactorCheck.Refused;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await SpendAsync(context, userId, match, cancellationToken))
        {
            logger.LogInformation("A two-factor check was refused, because the code was used while it ran");

            return TwoFactorCheck.Refused;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TwoFactorCheck.Passed;
    }

    public async Task<TwoFactorCheck> DisableAsync(Guid userId, string? code, CancellationToken cancellationToken)
    {
        var check = await CheckAsync(userId, code, cancellationToken);

        if (check != TwoFactorCheck.Passed)
        {
            return check;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var removed = await context.Database
            .SqlQuery<Guid>($"""DELETE FROM totp_authenticators WHERE user_id = {userId} RETURNING secret_id AS "Value" """)
            .ToListAsync(cancellationToken);

        if (removed is not [var secretId])
        {
            return TwoFactorCheck.NotRequired;
        }

        await RecoveryCodes.DeleteAllAsync(context, userId, cancellationToken);
        await SignInChallenges.DeleteAsync(context, userId, cancellationToken);
        await secrets.RemoveAsync(context, secretId, cancellationToken);

        audit.Add(context, AuditEntry.ByUser(AuditAction.TwoFactorDisabled, userId));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Turned two-factor off");

        return TwoFactorCheck.Passed;
    }

    public async Task<RegenerateRecoveryCodesResult> RegenerateRecoveryCodesAsync(Guid userId, string? code, CancellationToken cancellationToken)
    {
        var check = await CheckAsync(userId, code, cancellationToken);

        if (check != TwoFactorCheck.Passed)
        {
            return new RegenerateRecoveryCodesResult(check);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await IsEnabledAsync(context, userId, cancellationToken))
        {
            return new RegenerateRecoveryCodesResult(TwoFactorCheck.NotRequired);
        }

        var codes = RecoveryCodes.New();
        await RecoveryCodes.ReplaceAsync(context, userId, codes, cancellationToken);

        audit.Add(context, AuditEntry.ByUser(AuditAction.RecoveryCodesRegenerated, userId));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Replaced the recovery codes");

        return new RegenerateRecoveryCodesResult(TwoFactorCheck.Passed, new RecoveryCodeSet(codes));
    }

    public static Task<bool> IsEnabledAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        context.Set<TotpAuthenticator>().AnyAsync(row => row.UserId == userId && row.EnabledAt != null, cancellationToken);

    /// <summary>
    /// Whether <paramref name="code"/> is right for the user, without spending it, so the caller can spend
    /// it in the transaction that does what it allows. Null when it's wrong, already used, or two-factor
    /// is off. Charging for a guess is the caller's job, before calling this.
    /// </summary>
    public async Task<CodeMatch?> MatchAsync(EcnanibDbContext context, Guid userId, TwoFactorCode code, CancellationToken cancellationToken)
    {
        switch (code.Kind)
        {
            case TwoFactorCodeKind.Totp:
                var authenticator = await context.Set<TotpAuthenticator>()
                    .Where(row => row.UserId == userId && row.EnabledAt != null)
                    .Select(row => new { row.SecretId, row.LastUsedStep })
                    .SingleOrDefaultAsync(cancellationToken);

                if (authenticator is null)
                {
                    return null;
                }

                using (var key = await secrets.RevealAsync(authenticator.SecretId, cancellationToken))
                {
                    return Totp.Match(key, code.Value, Totp.StepAt(time.GetUtcNow()), authenticator.LastUsedStep) is { } step
                        ? new CodeMatch(TwoFactorCodeKind.Totp, step, null)
                        : null;
                }

            case TwoFactorCodeKind.Recovery:
                var hash = RecoveryCodes.HashOf(code.Value);

                return await context.Set<RecoveryCode>().AnyAsync(row => row.UserId == userId && row.CodeHash == hash, cancellationToken)
                    ? new CodeMatch(TwoFactorCodeKind.Recovery, 0, hash)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Spends a matched code in the caller's transaction, and clears the count of wrong ones. Moving the
    /// last used step forward is conditional, so of two requests with one code, only one gets through. A
    /// recovery code is deleted, and its use audited without saving. False when someone spent it first.
    /// </summary>
    public async Task<bool> SpendAsync(EcnanibDbContext context, Guid userId, CodeMatch match, CancellationToken cancellationToken)
    {
        if (match.Kind == TwoFactorCodeKind.Totp)
        {
            return await context.Set<TotpAuthenticator>()
                .Where(row => row.UserId == userId && row.EnabledAt != null && row.LastUsedStep < match.Step)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.LastUsedStep, match.Step)
                        .SetProperty(row => row.FailedChecks, 0)
                        .SetProperty(row => row.ChecksLockedUntil, (DateTimeOffset?)null),
                    cancellationToken) > 0;
        }

        if (!await RecoveryCodes.UseAsync(context, userId, match.RecoveryHash!, cancellationToken))
        {
            return false;
        }

        await context.Set<TotpAuthenticator>()
            .Where(row => row.UserId == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.FailedChecks, 0)
                    .SetProperty(row => row.ChecksLockedUntil, (DateTimeOffset?)null),
                cancellationToken);

        audit.Add(context, AuditEntry.ByUser(AuditAction.RecoveryCodeUsed, userId));
        logger.LogInformation("Used a recovery code");

        return true;
    }

    /// <summary>
    /// Charged before the code is checked, in one statement, like a sign-in attempt, so a lock window
    /// allows MaxFailedSignIns wrong codes however they arrive. A right code clears the count. False
    /// when checks are paused. Separate from the sign-in lockout, which strangers can trip.
    /// </summary>
    private async Task<bool> TryChargeCheckAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lockedUntil = now + settings.Lockout;
        var limit = settings.MaxFailedSignIns;

        var charged = await context.Set<TotpAuthenticator>()
            .Where(row => row.UserId == userId && row.EnabledAt != null && (row.ChecksLockedUntil == null || row.ChecksLockedUntil <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.FailedChecks, row => row.FailedChecks + 1 >= limit ? 0 : row.FailedChecks + 1)
                    .SetProperty(row => row.ChecksLockedUntil, row => row.FailedChecks + 1 >= limit ? lockedUntil : (DateTimeOffset?)null),
                cancellationToken);

        return charged > 0;
    }

    private static TotpSetup Setup(string email, SecretValue key)
    {
        var text = key.Reveal();
        var uri =
            $"otpauth://totp/{Issuer}:{Uri.EscapeDataString(email)}?secret={text}&issuer={Issuer}"
            + $"&algorithm=SHA1&digits={Totp.Digits}&period={Totp.StepSeconds}";

        return new TotpSetup(text, uri);
    }
}

/// <summary>A code that was right, not yet spent. For a recovery code, the hash to delete.</summary>
internal sealed record CodeMatch(TwoFactorCodeKind Kind, long Step, byte[]? RecoveryHash)
{
    public override string ToString() => Kind.ToString();
}
