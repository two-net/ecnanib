using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// FR-AUTH-06. Logs user IDs and never addresses. A reset ends every session, because whoever had the
/// old password may still be signed in. A link that came by email also verifies the address, which is
/// how the owner of an address takes back an account that a stranger registered with it first. See
/// Verification and resets in docs/architecture.md.
/// </summary>
internal sealed class PasswordResets(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IPasswordHasher hasher,
    Sessions sessions,
    IAdministrators administrators,
    IEmailSender sender,
    AccountEmails emails,
    IAuditTrail audit,
    IdentitySettings settings,
    ILogger<PasswordResets> logger) : IPasswordResets
{
    public PasswordResetMode Mode => settings.EmailEnabled ? PasswordResetMode.ByEmail : PasswordResetMode.ByAdministrator;

    /// <summary>
    /// An unknown address does less work than a known one: no row is written. The slow part, sending,
    /// runs in the background either way, so the difference is a database write. See Verification and
    /// resets in docs/architecture.md.
    /// </summary>
    public async Task RequestAsync(string email, CancellationToken cancellationToken)
    {
        if (!settings.EmailEnabled || !EmailAddresses.IsValid(email))
        {
            return;
        }

        var normalizedEmail = EmailAddresses.Normalize(email);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await context.Set<User>()
            .Where(candidate => candidate.NormalizedEmail == normalizedEmail)
            .Select(candidate => new { candidate.Id, candidate.Email })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            logger.LogInformation("A password reset was asked for an address with no account");
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = user.Id });

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var issued = await AccountTokens.IssueAsync(
            context,
            user.Id,
            AccountTokenPurpose.PasswordReset,
            AccountTokenDelivery.Email,
            AccountTokens.EmailedResetLifetime,
            AccountTokens.EmailThrottle,
            cancellationToken);

        if (issued is null)
        {
            logger.LogInformation("A password reset link wasn't sent, because one went out a few minutes ago");
            return;
        }

        // By a visitor, because asking proves nothing about who asked.
        audit.Add(context, new AuditEntry { Action = AuditAction.PasswordResetRequested, ActorKind = AuditActorKind.Visitor, UserId = user.Id });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The stored spelling, not what was typed, which only matches it once normalized.
        sender.Send(emails.PasswordReset(user.Email, issued.Token, issued.ExpiresAt));
        logger.LogInformation("Sent a password reset link");
    }

    public async Task<bool> IsLiveAsync(string? token, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await AccountTokens.IsLiveAsync(context, token, AccountTokenPurpose.PasswordReset, cancellationToken);
    }

    public async Task<PasswordResetResult> ResetAsync(string? token, string newPassword, CancellationToken cancellationToken)
    {
        if (newPassword.Length < settings.PasswordMinLength)
        {
            return PasswordResetResult.PasswordTooShort;
        }

        if (newPassword.Length > IdentitySettings.PasswordMaxLength)
        {
            return PasswordResetResult.PasswordTooLong;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Checked first, so a dead link costs no hash. It's used below, in the transaction.
        if (!await AccountTokens.IsLiveAsync(context, token, AccountTokenPurpose.PasswordReset, cancellationToken))
        {
            return PasswordResetResult.InvalidLink;
        }

        // Before the transaction, because a hash this slow mustn't hold one open.
        var newHash = await hasher.HashAsync(newPassword, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await AccountTokens.UseAsync(context, token, AccountTokenPurpose.PasswordReset, userId: null, cancellationToken) is not { } used)
        {
            // Used or expired while the hash ran.
            return PasswordResetResult.InvalidLink;
        }

        var userId = used.UserId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        // The hash first: a sign-in waits on this row, so a session it starts can't miss the delete below.
        await context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.PasswordHash, newHash)
                    .SetProperty(candidate => candidate.FailedSignIns, 0)
                    .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null),
                cancellationToken);

        if (used.ByEmail)
        {
            await EmailVerification.MarkVerifiedAsync(context, audit, userId, cancellationToken);
        }

        var ended = await sessions.DeleteAllAsync(context, userId, cancellationToken);

        audit.Add(context, AuditEntry.ByUser(AuditAction.PasswordReset, userId));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        sessions.EndConnections(ended);
        logger.LogInformation("Reset the password through a link, and ended {SessionCount} sessions", ended.Count);

        return PasswordResetResult.Reset;
    }

    public async Task<ResetLinkResult> IssueAsync(Guid administratorId, string email, CancellationToken cancellationToken)
    {
        if (settings.EmailEnabled || !await administrators.IsAdministratorAsync(administratorId, cancellationToken))
        {
            return new ResetLinkResult { Outcome = ResetLinkOutcome.NotAllowed };
        }

        if (!EmailAddresses.IsValid(email))
        {
            return new ResetLinkResult { Outcome = ResetLinkOutcome.NoAccount };
        }

        var normalizedEmail = EmailAddresses.Normalize(email);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var userId = await context.Set<User>()
            .Where(candidate => candidate.NormalizedEmail == normalizedEmail)
            .Select(candidate => (Guid?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is not { } targetId)
        {
            return new ResetLinkResult { Outcome = ResetLinkOutcome.NoAccount };
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = targetId });

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Not throttled: an administrator may need a second one, and only administrators can ask.
        var issued = await AccountTokens.IssueAsync(
            context,
            targetId,
            AccountTokenPurpose.PasswordReset,
            AccountTokenDelivery.Administrator,
            AccountTokens.AdministratorResetLifetime,
            throttle: null,
            cancellationToken) ?? throw new InvalidOperationException("An unthrottled token wasn't issued.");

        audit.Add(
            context,
            new AuditEntry
            {
                Action = AuditAction.PasswordResetLinkIssued,
                ActorKind = AuditActorKind.Administrator,
                ActorId = administratorId,
                UserId = targetId,
            });

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning("Administrator {AdministratorId} issued a password reset link", administratorId);

        return new ResetLinkResult
        {
            Outcome = ResetLinkOutcome.Issued,
            Link = emails.Link(AccountEmails.ResetPasswordPath, issued.Token),
            ExpiresAt = issued.ExpiresAt,
        };
    }
}
