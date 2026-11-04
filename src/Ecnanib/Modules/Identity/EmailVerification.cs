using System.Security.Claims;
using Ecnanib.Kernel;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// FR-AUTH-03. Logs user IDs and never addresses. A link verifies only for someone signed in to its
/// account, so the person who set the password is the one who verifies. Otherwise a stranger who
/// registered someone's address first would get a verified account the moment its owner clicked the
/// link. See Verification and resets in docs/architecture.md.
/// </summary>
internal sealed class EmailVerification(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IEmailSender sender,
    AccountEmails emails,
    IAuditTrail audit,
    IdentitySettings settings,
    ILogger<EmailVerification> logger) : IEmailVerification
{
    public bool IsEnabled => settings.EmailEnabled;

    public async Task<bool> IsSatisfiedAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.EmailVerifiedAt })
            .SingleOrDefaultAsync(cancellationToken);

        return user is not null && (!settings.EmailEnabled || user.EmailVerifiedAt is not null);
    }

    public async Task<VerificationEmailResult> SendAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!settings.EmailEnabled)
        {
            return VerificationEmailResult.Disabled;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.Email, candidate.EmailVerifiedAt })
            .SingleOrDefaultAsync(cancellationToken);

        // An account that's gone has nothing left to verify.
        if (user is null || user.EmailVerifiedAt is not null)
        {
            return VerificationEmailResult.AlreadyVerified;
        }

        if (await IssueAsync(context, userId, cancellationToken) is not { } issued)
        {
            return VerificationEmailResult.TooSoon;
        }

        Send(user.Email, issued);

        return VerificationEmailResult.Sent;
    }

    public async Task<EmailVerificationResult> VerifyAsync(ClaimsPrincipal user, string? token, CancellationToken cancellationToken)
    {
        if (AccountClaims.UserId(user) is not { } userId)
        {
            return EmailVerificationResult.InvalidLink;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await AccountTokens.UseAsync(context, token, AccountTokenPurpose.EmailVerification, userId, cancellationToken) is null)
        {
            logger.LogInformation("Address verification refused for a link that isn't live for this account");

            return EmailVerificationResult.InvalidLink;
        }

        await MarkVerifiedAsync(context, audit, userId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Verified the address");

        return EmailVerificationResult.Verified;
    }

    /// <summary>
    /// A new link, throttled, in the caller's context. Null when one went out a few minutes ago. Send it
    /// with <see cref="Send"/> once the caller has committed.
    /// </summary>
    public Task<IssuedToken?> IssueAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        AccountTokens.IssueAsync(
            context,
            userId,
            AccountTokenPurpose.EmailVerification,
            AccountTokenDelivery.Email,
            AccountTokens.VerificationLifetime,
            AccountTokens.EmailThrottle,
            cancellationToken);

    public void Send(string address, IssuedToken issued)
    {
        sender.Send(emails.Verification(address, issued.Token, issued.ExpiresAt));
        logger.LogInformation("Sent a link to verify the address");
    }

    /// <summary>
    /// Sets the address as verified, and records it, in the caller's context and without saving. An
    /// address that's already verified keeps its first time, and isn't recorded again.
    /// </summary>
    public static async Task MarkVerifiedAsync(EcnanibDbContext context, IAuditTrail audit, Guid userId, CancellationToken cancellationToken)
    {
        var verified = await context.Set<User>()
            .Where(candidate => candidate.Id == userId && candidate.EmailVerifiedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.EmailVerifiedAt, DateTimeOffset.UtcNow),
                cancellationToken);

        if (verified > 0)
        {
            audit.Add(context, AuditEntry.ByUser(AuditAction.EmailVerified, userId));
        }
    }
}
