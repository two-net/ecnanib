using System.Security.Cryptography;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>Signs browsers in and out. See docs/accounts.md.</summary>
/// <remarks>
/// A sign-in does the same work whatever is wrong with it: an address without an account is
/// checked against a hash that no password matches. So neither the answer nor the time it takes
/// tells whether an address has an account.
/// </remarks>
internal sealed class SignIn(
    IDbContextFactory<EcnanibDbContext> contexts,
    PasswordHasher passwords,
    Sessions sessions,
    TwoFactorCodes codes,
    TimeProvider clock,
    ILogger<SignIn> logger) : ISignIn
{
    // Made once, from text that nobody knows, with the parameters of the hashes of today.
    private Task<string>? _hashOfNoPassword;

    public async Task<SignInOutcome> SignInAsync(HttpContext request, string email, string password)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        var address = EmailAddress.Normalize(email);

        // What no account can have is refused for what it is, whether or not the other half
        // names an account: an address that registration would not take, no password at all,
        // or one so long that hashing it would cost more than the rules allow.
        if (!EmailAddress.LooksValid(address)
            || password.Length == 0
            || PasswordRules.ProblemOf(password) == PasswordProblem.TooLong)
        {
            return Failed();
        }

        // Awaited by every sign-in, so that the first one for an address without an account
        // does not take longer than the others by the time it takes to make this.
        var hashOfNoPassword = await HashOfNoPasswordAsync();

        var cancellationToken = request.RequestAborted;

        Guid? userId;
        string? passwordHash;
        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            var account = await context.Set<User>()
                .Where(user => user.Email == address)
                .Select(user => new { user.Id, user.PasswordHash })
                .SingleOrDefaultAsync(cancellationToken);

            userId = account?.Id;
            passwordHash = account?.PasswordHash;
        }

        var matches = await passwords.VerifyAsync(password, passwordHash ?? hashOfNoPassword);
        if (userId is null || passwordHash is null || !matches)
        {
            return Failed();
        }

        var started = await sessions.StartAsync(
            userId.Value,
            passwordHash,
            SessionCookie.TokenOf(request.Request),
            PendingSignInCookie.TokenOf(request.Request),
            AuditOrigin.Of(request),
            cancellationToken);

        if (started.PendingToken is { } pendingToken)
        {
            // The account has two-factor authentication. The browser is not signed in: it is
            // given the means to come back with a code, and nothing else.
            PendingSignInCookie.Set(request.Response, pendingToken);

            logger.LogInformation("User {UserId} gave their password and is asked for a code", userId);

            return SignInOutcome.CodeNeeded;
        }

        if (started.SessionToken is not { } token)
        {
            // The password was changed while it was being verified.
            return Failed();
        }

        // A sign-in that the browser had waiting for a code ended when the session started.
        PendingSignInCookie.Clear(request);
        SessionCookie.Set(request.Response, token);

        // The address is personal data, so the log names the account and not the address.
        logger.LogInformation("User {UserId} signed in", userId);

        return SignInOutcome.SignedIn;
    }

    public async Task<bool> AwaitsCodeAsync(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PendingSignInCookie.TokenOf(request.Request) is { } token
            && await sessions.FindWaitingAsync(token, request.RequestAborted) is not null;
    }

    public async Task<SignInCodeOutcome> CompleteAsync(HttpContext request, string code)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(code);

        var cancellationToken = request.RequestAborted;

        if (PendingSignInCookie.TokenOf(request.Request) is not { } pendingToken
            || await sessions.FindWaitingAsync(pendingToken, cancellationToken) is not { } waiting)
        {
            return StartAgain(request);
        }

        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row is locked from here to the end. A password reset and turning two-factor
        // authentication off both change the row and end the sign-ins that wait, so each either
        // waits for this and then ends the session it started, or came first.
        var account = await TwoFactorCodes.LockAsync(context, waiting.UserId, cancellationToken);

        // Looked at again under the lock: if one of them came first, the sign-in is gone.
        var stillWaits = await context.Set<PendingSignIn>()
            .AnyAsync(pending => pending.Id == waiting.Id && pending.ExpiresAt > now, cancellationToken);

        if (account is not { IsOn: true } || !stillWaits)
        {
            return StartAgain(request);
        }

        var answer = await codes.JudgeAsync(context, account, code, AuditOrigin.Of(request), now);
        if (answer != CodeAnswer.Right)
        {
            // The wrong code is counted in this transaction.
            await transaction.CommitAsync(CancellationToken.None);

            return answer == CodeAnswer.Refused ? SignInCodeOutcome.CodesRefused : SignInCodeOutcome.WrongCode;
        }

        // From here on nothing is canceled with the request: the code is spent. The session
        // starts in the transaction that accepted the code, and only there.
        var ended = await context.Set<PendingSignIn>()
            .Where(pending => pending.Id == waiting.Id)
            .ExecuteDeleteAsync(CancellationToken.None);

        if (ended != 1)
        {
            // Signing out, and a password given again on this browser, end the wait without
            // the lock on the account, so one of them can come in after the look above. Then
            // nothing waited for this code: the transaction is undone, and the code is not spent.
            return StartAgain(request);
        }

        var token = await sessions.AddAsync(
            context, account.UserId, SessionCookie.TokenOf(request.Request), now, AuditOrigin.Of(request), CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        PendingSignInCookie.Clear(request);
        SessionCookie.Set(request.Response, token);

        logger.LogInformation("User {UserId} signed in", account.UserId);

        return SignInCodeOutcome.SignedIn;
    }

    public async Task SignOutAsync(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A browser that gave a password and then signs out is not waiting for a code any more.
        if (PendingSignInCookie.TokenOf(request.Request) is { } pendingToken)
        {
            await sessions.EndWaitingAsync(pendingToken, request.RequestAborted);
            PendingSignInCookie.Clear(request);
        }

        if (SessionCookie.TokenOf(request.Request) is not { } token)
        {
            return;
        }

        if (await sessions.EndAsync(token, request.RequestAborted) is { } userId)
        {
            logger.LogInformation("User {UserId} signed out", userId);
        }

        SessionCookie.Clear(request.Response);
    }

    private Task<string> HashOfNoPasswordAsync()
    {
        var hash = Volatile.Read(ref _hashOfNoPassword);

        // Made again when making it failed, so that one failure does not fail every sign-in after it.
        if (hash is null || hash.IsFaulted || hash.IsCanceled)
        {
            hash = passwords.HashAsync(RandomNumberGenerator.GetHexString(32));
            Volatile.Write(ref _hashOfNoPassword, hash);
        }

        return hash;
    }

    private SignInOutcome Failed()
    {
        logger.LogInformation("A sign-in failed: the address or the password was not right");

        return SignInOutcome.Failed;
    }

    /// <summary>No sign-in of the browser waits for a code, so its cookie names nothing.</summary>
    private static SignInCodeOutcome StartAgain(HttpContext request)
    {
        PendingSignInCookie.Clear(request);

        return SignInCodeOutcome.StartAgain;
    }
}
