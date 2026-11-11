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
    ILogger<SignIn> logger) : ISignIn
{
    // Made once, from text that nobody knows, with the parameters of the hashes of today.
    private Task<string>? _hashOfNoPassword;

    public async Task<bool> SignInAsync(HttpContext request, string email, string password)
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

        var token = await sessions.StartAsync(
            userId.Value, passwordHash, SessionCookie.TokenOf(request.Request), cancellationToken);
        if (token is null)
        {
            // The password was changed while it was being verified.
            return Failed();
        }

        SessionCookie.Set(request.Response, token);

        // The address is personal data, so the log names the account and not the address.
        logger.LogInformation("User {UserId} signed in", userId);

        return true;
    }

    public async Task SignOutAsync(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

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

    private bool Failed()
    {
        logger.LogInformation("A sign-in failed: the address or the password was not right");

        return false;
    }
}
