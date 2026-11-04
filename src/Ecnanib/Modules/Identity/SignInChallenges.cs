using System.Buffers.Text;
using System.Security.Cryptography;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Sign-ins waiting for a two-factor code. A challenge token is 32 random bytes in base64url, like a
/// link's, and only its hash is stored. Each takes the caller's context. See Two-factor in
/// docs/architecture.md.
/// </summary>
internal static class SignInChallenges
{
    /// <summary>Long enough to find a phone and open an app, and short enough that a password alone is soon worth nothing again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A new challenge, replacing any the account has. Null when the password is no longer
    /// <paramref name="verifiedHash"/>. One statement under a row lock, as a session starts, so a
    /// password change that finishes while the password was being verified can't leave a challenge it
    /// never saw: the change deletes challenges after it replaces the hash.
    /// </summary>
    public static async Task<IssuedToken?> IssueAsync(
        EcnanibDbContext context,
        Guid userId,
        string verifiedHash,
        CancellationToken cancellationToken)
    {
        var bytes = RandomNumberGenerator.GetBytes(AccountTokens.TokenLength);
        var hash = SHA256.HashData(bytes);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + Lifetime;

        var issued = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO sign_in_challenges (user_id, token_hash, created_at, expires_at)
            SELECT id, {hash}, {now}, {expiresAt}
            FROM users
            WHERE id = {userId} AND password_hash = {verifiedHash}
            FOR SHARE
            ON CONFLICT (user_id) DO UPDATE
            SET token_hash = excluded.token_hash, created_at = excluded.created_at, expires_at = excluded.expires_at
            """,
            cancellationToken);

        return issued > 0 ? new IssuedToken(Base64Url.EncodeToString(bytes), expiresAt) : null;
    }

    /// <summary>Whose live challenge this is, without using it, or null.</summary>
    public static async Task<Guid?> FindAsync(EcnanibDbContext context, string? token, CancellationToken cancellationToken)
    {
        if (AccountTokens.HashOf(token) is not { } hash)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        return await context.Set<SignInChallenge>()
            .Where(challenge => challenge.TokenHash == hash && challenge.ExpiresAt > now)
            .Select(challenge => (Guid?)challenge.UserId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes a live challenge of <paramref name="userId"/>'s, so it works once. False when there's none,
    /// because it expired, was replaced, or a password change or reset ended it. Call it in the
    /// transaction that starts the session.
    /// </summary>
    public static async Task<bool> UseAsync(EcnanibDbContext context, string? token, Guid userId, CancellationToken cancellationToken)
    {
        if (AccountTokens.HashOf(token) is not { } hash)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        return await context.Set<SignInChallenge>()
            .Where(challenge => challenge.TokenHash == hash && challenge.UserId == userId && challenge.ExpiresAt > now)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }

    /// <summary>Ends a sign-in in progress, as a password change, a reset or turning two-factor off must.</summary>
    public static Task<int> DeleteAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        context.Set<SignInChallenge>().Where(challenge => challenge.UserId == userId).ExecuteDeleteAsync(cancellationToken);
}
