using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// Takes the proof of a user who is signed in that they are who they are, and says whether one
/// was given recently enough for a sensitive action (FR-ACC-4). See docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// The proof is kept in the row of the session whose browser gave it, and nowhere else. So it is
/// of no use to another browser of the same user, and whatever ends the session ends it too:
/// signing out, staying away, a new password.
/// </para>
/// <para>
/// For an account with two-factor authentication the code is judged before anything is said
/// about the password. Someone at a browser that was left signed in, who does not have the app,
/// is told that the code is wrong whatever password they try, and their wrong codes are counted.
/// So the form does not help them to find the password.
/// </para>
/// <para>
/// For an account without it, the form would tell such a person the password by trying. So the
/// password of an account can be tried here only so often (NFR-SEC-4), as on the sign-in form.
/// </para>
/// </remarks>
internal sealed class Reauthentication(
    IDbContextFactory<EcnanibDbContext> contexts,
    PasswordHasher passwords,
    Sessions sessions,
    TwoFactorCodes codes,
    AttemptLimits attempts,
    TimeProvider clock,
    ILogger<Reauthentication> logger) : IReauthentication
{
    public async Task<bool> IsRecentAsync(HttpContext request, SensitiveAction action)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Nobody is signed in, so nobody proved anything.
        if (SignedInUser.IdOf(request) is not { } userId)
        {
            return false;
        }

        if (await IsRecentAsync(request, userId))
        {
            return true;
        }

        LogAsked(userId, action);

        return false;
    }

    /// <summary>
    /// The same question, for a sensitive action of Accounts itself, asked inside the
    /// transaction that does the action. The answer then holds until the action is done: with
    /// the lock on the account's row, nothing that ends the proof comes in between.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="request">The request that asks for the action.</param>
    /// <param name="action">The action that is asked for.</param>
    internal async Task<bool> IsRecentAsync(EcnanibDbContext context, HttpContext request, SensitiveAction action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (SignedInUser.IdOf(request) is not { } userId)
        {
            return false;
        }

        if (SessionCookie.TokenOf(request.Request) is { } token
            && IsRecent(await Sessions.ReauthenticatedAtAsync(context, token, userId, request.RequestAborted)))
        {
            return true;
        }

        LogAsked(userId, action);

        return false;
    }

    public async Task<ReauthenticationState> StateAsync(HttpContext request)
    {
        var userId = SignedInUser.RequiredIdOf(request);

        bool asksForCode;
        await using (var context = await contexts.CreateDbContextAsync(request.RequestAborted))
        {
            asksForCode = await context.Set<User>()
                .AnyAsync(user => user.Id == userId && user.TwoFactorEnabledAt != null, request.RequestAborted);
        }

        return new ReauthenticationState(asksForCode, await IsRecentAsync(request, userId));
    }

    public async Task<ReauthenticationOutcome> ConfirmAsync(HttpContext request, string password, string code)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(code);

        var userId = SignedInUser.RequiredIdOf(request);
        var cancellationToken = request.RequestAborted;

        // The proof is written into the session of the browser that gives it.
        if (SessionCookie.TokenOf(request.Request) is not { } token)
        {
            return ReauthenticationOutcome.SessionEnded;
        }

        // Before anything is looked up, hashed, or judged: a proof that is refused spends no code.
        if (attempts.PasswordOf(userId) is var attempt and not Attempt.Allowed)
        {
            logger.LogInformation(
                "User {UserId} was refused confirming who they are: too many attempts {Limit}",
                userId,
                AttemptLimits.Describe(attempt));

            return ReauthenticationOutcome.TooManyAttempts;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var verifiedHash = await context.Set<User>()
            .Where(user => user.Id == userId)
            .Select(user => user.PasswordHash)
            .SingleOrDefaultAsync(cancellationToken);

        if (verifiedHash is null)
        {
            return ReauthenticationOutcome.SessionEnded;
        }

        // Before the transaction, because a hash takes its time, and the row is not locked for
        // it. What no account can have as its password is not hashed at all.
        var passwordIsRight = password.Length > 0
            && PasswordRules.ProblemOf(password) != PasswordProblem.TooLong
            && await passwords.VerifyAsync(password, verifiedHash);

        var now = clock.GetUtcNow();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row is locked from here to the end, as the first thing this transaction asks of it.
        // So what it says about two-factor authentication and the password holds until the proof
        // is written down.
        if (await TwoFactorCodes.LockAsync(context, userId, cancellationToken) is not { } account)
        {
            return ReauthenticationOutcome.SessionEnded;
        }

        if (account.IsOn)
        {
            // The code first, and its answer whatever the password was: see the remarks above.
            var answer = await codes.JudgeAsync(context, account, code, AuditOrigin.Of(request), now);
            if (answer != CodeAnswer.Right)
            {
                // The wrong code is counted in this transaction.
                await transaction.CommitAsync(CancellationToken.None);

                return answer == CodeAnswer.Refused ? ReauthenticationOutcome.CodesRefused : ReauthenticationOutcome.WrongCode;
            }
        }

        // From here on nothing is canceled with the request: a code may be spent.
        var passwordStands = await context.Set<User>()
            .AnyAsync(user => user.Id == userId && user.PasswordHash == verifiedHash, CancellationToken.None);

        if (!passwordIsRight || !passwordStands)
        {
            // A code that was right stays spent. If it could be given again, one code would
            // serve for as many passwords as can be tried while it is the code of the app.
            await transaction.CommitAsync(CancellationToken.None);

            logger.LogInformation("User {UserId} did not confirm who they are: the password was not right", userId);

            return ReauthenticationOutcome.WrongPassword;
        }

        if (!await Sessions.MarkReauthenticatedAsync(context, token, userId, now, CancellationToken.None))
        {
            // The session ended since the request was signed in with it: the user signed out on
            // another tab, or their password was set anew. Nothing is left to hold the proof,
            // so the transaction is undone, and a code is not spent.
            return ReauthenticationOutcome.SessionEnded;
        }

        // In the transaction that wrote the proof down, so that the two stand or fall together.
        await AuditTrail.RecordAsync(
            context,
            userId,
            AuditAction.Reauthenticated,
            AuditTarget.Account(userId),
            now,
            AuditOrigin.Of(request),
            CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        logger.LogInformation("User {UserId} confirmed who they are", userId);

        return ReauthenticationOutcome.Confirmed;
    }

    /// <summary>Whether the session of a request holds a proof that is recent enough, as the database says now.</summary>
    private async Task<bool> IsRecentAsync(HttpContext request, Guid userId)
    {
        if (SessionCookie.TokenOf(request.Request) is not { } token)
        {
            return false;
        }

        return IsRecent(await sessions.ReauthenticatedAtAsync(token, userId, request.RequestAborted));
    }

    /// <summary>Whether a proof that was given at a time still holds. One that was never given does not.</summary>
    private bool IsRecent(DateTimeOffset? provedAt) =>
        provedAt is { } at && clock.GetUtcNow() - at < ReauthenticationRules.HoldsFor;

    private void LogAsked(Guid userId, SensitiveAction action) =>
        logger.LogInformation("User {UserId} is asked to confirm who they are before {Action}", userId, action);
}
