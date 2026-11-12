using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>Sends password reset links and sets the passwords chosen with them. See docs/accounts.md.</summary>
/// <remarks>
/// A reset does not confirm the email address of an account. Only a link from the registration
/// form does, because that form is where the legal documents are accepted.
/// </remarks>
internal sealed class PasswordReset(
    ApplicationSettings application,
    IDbContextFactory<EcnanibDbContext> contexts,
    PasswordHasher passwords,
    IAccountMailer mailer,
    AttemptLimits attempts,
    TimeProvider clock,
    ILogger<PasswordReset> logger) : IPasswordReset
{
    public async Task<PasswordResetRequestOutcome> RequestAsync(
        string email, AuditOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(origin);

        var address = EmailAddress.Normalize(email);
        if (!EmailAddress.LooksValid(address))
        {
            return PasswordResetRequestOutcome.Invalid;
        }

        // Before a link is stored and an email is sent, and for the address as it is, with an
        // account or without one. A request that is refused replaces no link.
        if (attempts.PasswordReset(origin, address) is var attempt and not Attempt.Allowed)
        {
            logger.LogInformation("A password reset was refused: too many attempts {Limit}", AttemptLimits.Describe(attempt));

            return PasswordResetRequestOutcome.TooManyAttempts;
        }

        var (token, tokenHash) = SecretToken.Create();
        var expiresAt = clock.GetUtcNow() + PasswordResetLink.Lifetime;

        Guid? userId;
        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            userId = await context.Set<User>()
                .Where(user => user.Email == address)
                .Select(user => (Guid?)user.Id)
                .SingleOrDefaultAsync(cancellationToken);

            // A later link ends the earlier one. The statement runs whether the address has an
            // account or not, so that the work done is the same.
            await context.Set<User>()
                .Where(user => user.Email == address)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(user => user.PasswordResetTokenHash, (byte[]?)tokenHash)
                        .SetProperty(user => user.PasswordResetExpiresAt, (DateTimeOffset?)expiresAt),
                    cancellationToken);
        }

        // One email either way, after the link is stored: a link must not arrive before it works.
        try
        {
            if (userId is null)
            {
                await mailer.SendNoAccountNoticeAsync(address, cancellationToken);
            }
            else
            {
                await mailer.SendPasswordResetLinkAsync(
                    address, PasswordResetLink.For(application.PublicUrl, token), cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Asking again sends a new link.
            logger.LogError(error, "The email of a password reset could not be sent");

            return PasswordResetRequestOutcome.EmailNotSent;
        }

        // The address is personal data, so the log names the account and not the address.
        if (userId is null)
        {
            logger.LogInformation("A password reset was asked for an address that has no account. Its owner was told so");
        }
        else
        {
            logger.LogInformation("A password reset link was sent to user {UserId}", userId);
        }

        return PasswordResetRequestOutcome.Accepted;
    }

    public async Task<PasswordResetOutcome> ResetAsync(
        string token, string newPassword, AuditOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(newPassword);
        ArgumentNullException.ThrowIfNull(origin);

        if (SecretToken.HashOf(token) is not { } tokenHash)
        {
            return PasswordResetOutcome.LinkNotValid;
        }

        var now = clock.GetUtcNow();
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Before the password is looked at, so that a link that does not work gets one answer,
        // and no password is hashed for it.
        var account = await context.Set<User>()
            .Where(user => user.PasswordResetTokenHash == tokenHash && user.PasswordResetExpiresAt > now)
            .Select(user => new { user.Id, user.Email })
            .SingleOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            return PasswordResetOutcome.LinkNotValid;
        }

        switch (PasswordRules.ProblemOf(newPassword))
        {
            case PasswordProblem.TooShort:
                return PasswordResetOutcome.PasswordTooShort;

            case PasswordProblem.TooLong:
                return PasswordResetOutcome.PasswordTooLong;
        }

        var passwordHash = await passwords.HashAsync(newPassword);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // One statement, and only while the link still stands: of two requests with the same
        // link, one sets its password and the other finds the link used.
        var changed = await context.Set<User>()
            .Where(user => user.Id == account.Id
                && user.PasswordResetTokenHash == tokenHash
                && user.PasswordResetExpiresAt > now)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(user => user.PasswordHash, passwordHash)
                    .SetProperty(user => user.PasswordResetTokenHash, (byte[]?)null)
                    .SetProperty(user => user.PasswordResetExpiresAt, (DateTimeOffset?)null)

                    // A verification link that sets the password would undo this for whoever
                    // holds it, so it ends too. One that only verifies the address stands.
                    .SetProperty(
                        user => user.EmailVerificationTokenHash,
                        user => user.EmailVerificationSetsPassword ? null : user.EmailVerificationTokenHash)
                    .SetProperty(
                        user => user.EmailVerificationExpiresAt,
                        user => user.EmailVerificationSetsPassword ? null : user.EmailVerificationExpiresAt)
                    .SetProperty(user => user.EmailVerificationSetsPassword, false),
                cancellationToken);

        if (changed != 1)
        {
            return PasswordResetOutcome.LinkNotValid;
        }

        // Whoever was signed in knew the earlier password, or held a browser of someone who did.
        var ended = await Sessions.EndAllAsync(context, account.Id, cancellationToken);

        // In the transaction that set the password, so that the two stand or fall together.
        await AuditTrail.RecordAsync(
            context, account.Id, AuditAction.PasswordChanged, AuditTarget.Account(account.Id), now, origin, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserId} set a new password with a reset link, which ended {SessionCount} sessions", account.Id, ended);

        // So that an owner who did not do this finds out. The password is set by now, so the
        // notice goes out even when whoever set it has closed the connection.
        try
        {
            await mailer.SendPasswordChangedNoticeAsync(account.Email, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The password is set all the same.
            logger.LogError(error, "The notice of a changed password could not be sent");
        }

        return PasswordResetOutcome.Done;
    }
}
