using System.Security.Cryptography;
using System.Text;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>The rules of two-factor authentication that a page tells its user about. See docs/accounts.md.</summary>
public static class TwoFactorRules
{
    /// <summary>
    /// How many wrong codes in a row an account takes. With the last of them, every code of the
    /// account is refused for <see cref="RefusedFor"/>, so that a code cannot be found by trying.
    /// </summary>
    public const int WrongCodesAllowed = 5;

    /// <summary>How long every code of an account is refused after too many wrong ones.</summary>
    public static readonly TimeSpan RefusedFor = TimeSpan.FromMinutes(15);

    /// <summary>How many recovery codes an account is given.</summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>
    /// How long the key on the page that turns two-factor authentication on can be confirmed.
    /// After that the page offers another, so that a page which was left open, by whoever was at
    /// the browser, is of no use to them later.
    /// </summary>
    public static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many steps before and after the present one a code from the app may be of, for a
    /// clock that is a little off and for the time it takes to type.
    /// </summary>
    public const int StepsEitherSide = 1;
}

/// <summary>What a code that someone entered turned out to be.</summary>
internal enum CodeAnswer
{
    /// <summary>A code from the app that was not used before, or a recovery code, which is now spent.</summary>
    Right,

    /// <summary>Not right: wrong, used before, or not a code at all.</summary>
    Wrong,

    /// <summary>Every code of the account is refused for now, after too many wrong ones.</summary>
    Refused,
}

/// <summary>What an account stores for two-factor authentication, as it was read under the lock on its row.</summary>
internal sealed record TwoFactorAccount(
    Guid UserId,
    byte[]? Secret,
    string? SecretKeyId,
    DateTimeOffset? EnabledAt,
    long? LastStep,
    int FailedCodes,
    DateTimeOffset? LockedUntil)
{
    /// <summary>Whether two-factor authentication is on for the account.</summary>
    public bool IsOn => EnabledAt is not null && Secret is not null && SecretKeyId is not null;
}

/// <summary>
/// Judges a code for every place that takes one: signing in, turning two-factor authentication
/// off, making new recovery codes (FR-ACC-3), and proving who one is again before a sensitive
/// action (FR-ACC-4). A code is one from the authenticator app or a recovery code. See
/// docs/accounts.md.
/// </summary>
/// <remarks>
/// A code is judged inside a transaction that holds the lock on the account's row, which
/// <see cref="LockAsync"/> takes. So two requests with the same code are judged one after the
/// other, and the second finds the code used. The caller commits the transaction whatever the
/// answer is: a wrong code is counted in it.
/// </remarks>
internal sealed class TwoFactorCodes(StoredSecrets secrets, ILogger<TwoFactorCodes> logger)
{
    /// <summary>What the stored key of an account is bound to, so that it decrypts for that account only.</summary>
    public static string BoundTo(Guid userId) => $"two-factor:{userId}";

    /// <summary>
    /// Locks the row of an account for the rest of the transaction, and reads what it stores for
    /// two-factor authentication. The lock is the first thing the transaction asks of the row,
    /// and it is the lock of a change, so that no transaction waits for another that waits for it.
    /// </summary>
    /// <returns>Null when the account does not exist.</returns>
    public static async Task<TwoFactorAccount?> LockAsync(
        EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        var locked = await context.Database
            .SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM users
                WHERE id = {userId}
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return null;
        }

        // Read after the lock, so that this is what the row holds until the transaction ends.
        return await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => new TwoFactorAccount(
                user.Id,
                user.TwoFactorSecret,
                user.TwoFactorSecretKeyId,
                user.TwoFactorEnabledAt,
                user.TwoFactorLastStep,
                user.TwoFactorFailedCodes,
                user.TwoFactorLockedUntil))
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Judges a code for an account that has two-factor authentication on, and stores what
    /// follows from it: the step of an accepted code, the end of a recovery code, or one more
    /// wrong code.
    /// </summary>
    /// <param name="context">The context that holds the transaction and the lock.</param>
    /// <param name="account">The account, as <see cref="LockAsync"/> read it in this transaction.</param>
    /// <param name="code">What was entered.</param>
    /// <param name="origin">Where the request came from, for the audit trail.</param>
    /// <param name="now">The time the code is judged at.</param>
    public async Task<CodeAnswer> JudgeAsync(
        EcnanibDbContext context, TwoFactorAccount account, string code, string? origin, DateTimeOffset now)
    {
        // Without a look at the code, so that guessing gains nothing while it lasts.
        if (account.LockedUntil > now)
        {
            return CodeAnswer.Refused;
        }

        // Nothing here is canceled with the request: a code that was judged is counted.
        var typed = WithoutSpaces(code);
        if (LooksLikeAppCode(typed))
        {
            var key = secrets.Decrypt(new EncryptedSecret(account.SecretKeyId!, account.Secret!), BoundTo(account.UserId));
            long? step;
            bool usedBefore;
            try
            {
                step = StepOf(key, typed, now, account.LastStep, out usedBefore);
            }
            finally
            {
                // The key was needed for this, and is not kept (FR-KEY-9).
                CryptographicOperations.ZeroMemory(key);
            }

            if (step is not null)
            {
                await context.Set<User>()
                    .Where(user => user.Id == account.UserId)
                    .ExecuteUpdateAsync(row => row
                        .SetProperty(user => user.TwoFactorLastStep, step)
                        .SetProperty(user => user.TwoFactorFailedCodes, 0));

                return CodeAnswer.Right;
            }

            if (usedBefore)
            {
                // The right code, a second time: a form that was sent again, or someone who
                // watched it being typed. It is refused, and tells nothing about other codes.
                return CodeAnswer.Wrong;
            }

            return await CountAsync(context, account, now);
        }

        if (RecoveryCodes.HashOf(typed) is { } codeHash)
        {
            // Deleted as it is found: of two requests with one code, one finds it gone.
            var spent = await context.Set<RecoveryCode>()
                .OwnedBy(account.UserId)
                .Where(recovery => recovery.CodeHash == codeHash)
                .ExecuteDeleteAsync();

            if (spent != 1)
            {
                return await CountAsync(context, account, now);
            }

            if (account.FailedCodes > 0)
            {
                await context.Set<User>()
                    .Where(user => user.Id == account.UserId)
                    .ExecuteUpdateAsync(row => row.SetProperty(user => user.TwoFactorFailedCodes, 0));
            }

            await AuditTrail.RecordAsync(
                context,
                account.UserId,
                AuditAction.TwoFactorRecoveryCodeUsed,
                AuditTrail.Account(account.UserId),
                now,
                origin,
                CancellationToken.None);

            logger.LogInformation("User {UserId} used a recovery code", account.UserId);

            return CodeAnswer.Right;
        }

        // What cannot be a code is not a guess at one, and is not counted.
        return CodeAnswer.Wrong;
    }

    /// <summary>
    /// The step of time that a code from the app is the code of, among the present step and the
    /// ones around it, when it is later than the last step a code was accepted for. When it is
    /// the code of several of them, the latest.
    /// </summary>
    /// <param name="key">The key the account shares with the app.</param>
    /// <param name="code">Six digits.</param>
    /// <param name="now">The present time.</param>
    /// <param name="lastStep">The step of the last code that was accepted, if any.</param>
    /// <param name="usedBefore">True when the code is right for a step that is not later than that.</param>
    public static long? StepOf(ReadOnlySpan<byte> key, string code, DateTimeOffset now, long? lastStep, out bool usedBefore)
    {
        usedBefore = false;
        long? accepted = null;

        Span<byte> typed = stackalloc byte[Totp.Digits];
        Span<byte> expected = stackalloc byte[Totp.Digits];
        Encoding.ASCII.GetBytes(code, typed);

        var present = Totp.StepAt(now);
        for (var step = present - TwoFactorRules.StepsEitherSide; step <= present + TwoFactorRules.StepsEitherSide; step++)
        {
            Encoding.ASCII.GetBytes(Totp.CodeAt(key, step), expected);

            // Every step is compared, in the same time whatever the digits are.
            if (!CryptographicOperations.FixedTimeEquals(typed, expected))
            {
                continue;
            }

            if (step <= lastStep)
            {
                usedBefore = true;
            }
            else
            {
                // The latest step it is the code of. Two steps can have the same code, and the
                // code must not be accepted again for the later one.
                accepted = step;
            }
        }

        return accepted;
    }

    /// <summary>Whether a text has the form of a code from the app: six digits.</summary>
    public static bool LooksLikeAppCode(string text) => text.Length == Totp.Digits && text.All(char.IsAsciiDigit);

    /// <summary>What was entered without its spaces: an app shows a code as two groups of three.</summary>
    public static string WithoutSpaces(string code) => string.Concat(code.Where(character => !char.IsWhiteSpace(character)));

    /// <summary>Counts one more wrong code, and refuses every code for a while when it is one too many.</summary>
    private async Task<CodeAnswer> CountAsync(EcnanibDbContext context, TwoFactorAccount account, DateTimeOffset now)
    {
        var failed = account.FailedCodes + 1;
        var theAccount = context.Set<User>().Where(user => user.Id == account.UserId);

        if (failed < TwoFactorRules.WrongCodesAllowed)
        {
            await theAccount.ExecuteUpdateAsync(row => row.SetProperty(user => user.TwoFactorFailedCodes, failed));

            logger.LogInformation("A code of user {UserId} was not right", account.UserId);

            return CodeAnswer.Wrong;
        }

        // The count starts again, so that the first code after the wait is not one too many.
        var until = now + TwoFactorRules.RefusedFor;
        await theAccount.ExecuteUpdateAsync(row => row
            .SetProperty(user => user.TwoFactorFailedCodes, 0)
            .SetProperty(user => user.TwoFactorLockedUntil, (DateTimeOffset?)until));

        logger.LogWarning(
            "The codes of user {UserId} are refused for {Minutes} minutes, after {WrongCodes} wrong ones in a row",
            account.UserId,
            (int)TwoFactorRules.RefusedFor.TotalMinutes,
            failed);

        return CodeAnswer.Refused;
    }
}
