using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Everything that guards a password, for sign-in, changing it and turning two-factor on. Each takes the
/// caller's context. See Lockout in docs/architecture.md.
/// </summary>
internal sealed class PasswordChecks(IPasswordHasher hasher, IdentitySettings settings)
{
    /// <summary>
    /// Charges an attempt, then verifies. A locked account spends a hash, so it takes as long as a wrong
    /// password. A right one clears the count when <paramref name="clearFailures"/> says so. Sign-in to an
    /// account with two-factor doesn't, because the password is only half of it: the count clears when the
    /// code is right, or a right password between wrong codes would allow endless guesses.
    /// </summary>
    public async Task<PasswordCheck> CheckAsync(
        EcnanibDbContext context,
        Guid userId,
        string password,
        string hash,
        bool clearFailures,
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

        if (clearFailures)
        {
            await ClearFailuresAsync(context, userId, cancellationToken);
        }

        return PasswordCheck.Right;
    }

    /// <summary>
    /// Charged before the secret is checked and in one statement, so parallel guesses can't all pass the
    /// check during a slow hash, and a lock window allows MaxFailedSignIns attempts however they arrive.
    /// A row lock queues concurrent attempts, and each then sees the last one's result. False when the
    /// account is locked. Attempts during a lock change nothing, but someone can lock an account again
    /// as soon as a lock ends: see Lockout in docs/architecture.md. A two-factor code at sign-in is
    /// charged here too, so passwords and codes share one budget.
    /// </summary>
    public async Task<bool> TryChargeAttemptAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
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

    public static Task<int> ClearFailuresAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        context.Set<User>()
            .Where(candidate => candidate.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.FailedSignIns, 0)
                    .SetProperty(candidate => candidate.LockedUntil, (DateTimeOffset?)null),
                cancellationToken);
}

internal enum PasswordCheck
{
    Right,
    Wrong,
    Locked,
}
