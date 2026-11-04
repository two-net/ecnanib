using System.Buffers.Text;
using System.Security.Cryptography;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// Issues and uses the tokens in emailed and administrator-issued links. Each takes the caller's
/// context, so that using a token commits with the change it allows. A token is 32 random bytes in
/// base64url, and only its hash is stored. See Verification and resets in docs/architecture.md.
/// </summary>
internal static class AccountTokens
{
    public const int HashLength = 32;

    /// <summary>The SRS asks for links that are time-limited, not configurable.</summary>
    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);

    public static readonly TimeSpan EmailedResetLifetime = TimeSpan.FromHours(1);

    /// <summary>Longer, because an administrator hands it over out of band, which can take a while.</summary>
    public static readonly TimeSpan AdministratorResetLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// How soon another email of the same kind can go to one account. The request limiter only sees
    /// the sender's address, so without this anyone could fill someone's inbox. A request inside this
    /// window sends nothing, and the link already sent keeps working.
    /// </summary>
    public static readonly TimeSpan EmailThrottle = TimeSpan.FromMinutes(5);

    private const int TokenLength = 32;

    /// <summary>
    /// A new token, replacing any the account already has for <paramref name="purpose"/>. Null, changing
    /// nothing, when the existing one is younger than <paramref name="throttle"/>. One statement, so two
    /// requests at once can't both get past the throttle. Send it only once the caller has committed.
    /// </summary>
    /// <param name="throttle">Null to replace the existing token whatever its age.</param>
    public static async Task<IssuedToken?> IssueAsync(
        EcnanibDbContext context,
        Guid userId,
        AccountTokenPurpose purpose,
        AccountTokenDelivery delivery,
        TimeSpan lifetime,
        TimeSpan? throttle,
        CancellationToken cancellationToken)
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenLength);
        var hash = SHA256.HashData(bytes);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + lifetime;

        var issued = throttle is { } window
            ? await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO account_tokens (user_id, purpose, token_hash, delivery, created_at, expires_at)
                VALUES ({userId}, {purpose.ToString()}, {hash}, {delivery.ToString()}, {now}, {expiresAt})
                ON CONFLICT (user_id, purpose) DO UPDATE
                SET token_hash = excluded.token_hash, delivery = excluded.delivery,
                    created_at = excluded.created_at, expires_at = excluded.expires_at
                WHERE account_tokens.created_at <= {now - window}
                """,
                cancellationToken)
            : await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO account_tokens (user_id, purpose, token_hash, delivery, created_at, expires_at)
                VALUES ({userId}, {purpose.ToString()}, {hash}, {delivery.ToString()}, {now}, {expiresAt})
                ON CONFLICT (user_id, purpose) DO UPDATE
                SET token_hash = excluded.token_hash, delivery = excluded.delivery,
                    created_at = excluded.created_at, expires_at = excluded.expires_at
                """,
                cancellationToken);

        return issued > 0 ? new IssuedToken(Base64Url.EncodeToString(bytes), expiresAt) : null;
    }

    /// <summary>
    /// Deletes a live token and says whose it was, or null when it doesn't exist, has expired, or isn't
    /// <paramref name="userId"/>'s. A token that isn't this user's is left alone, so it still works for
    /// the account it was sent to. Call it in the transaction that makes the change the token allows.
    /// </summary>
    public static async Task<UsedToken?> UseAsync(
        EcnanibDbContext context,
        string? token,
        AccountTokenPurpose purpose,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        if (HashOf(token) is not { } hash)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var email = AccountTokenDelivery.Email.ToString();

        // Two statements rather than "@user IS NULL OR", which PostgreSQL can't type for a null uuid.
        var query = userId is { } owner
            ? context.Database.SqlQuery<UsedToken>(
                $"""
                DELETE FROM account_tokens
                WHERE token_hash = {hash} AND purpose = {purpose.ToString()} AND expires_at > {now} AND user_id = {owner}
                RETURNING user_id, delivery = {email} AS by_email
                """)
            : context.Database.SqlQuery<UsedToken>(
                $"""
                DELETE FROM account_tokens
                WHERE token_hash = {hash} AND purpose = {purpose.ToString()} AND expires_at > {now}
                RETURNING user_id, delivery = {email} AS by_email
                """);

        return (await query.ToListAsync(cancellationToken)).SingleOrDefault();
    }

    /// <summary>Whether a link would still work, without using it, so a page can say so before asking for anything.</summary>
    public static async Task<bool> IsLiveAsync(
        EcnanibDbContext context,
        string? token,
        AccountTokenPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (HashOf(token) is not { } hash)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        return await context.Set<AccountToken>()
            .AnyAsync(row => row.TokenHash == hash && row.Purpose == purpose && row.ExpiresAt > now, cancellationToken);
    }

    /// <summary>Ends any outstanding link of this kind, such as a reset link once the password has changed.</summary>
    public static Task<int> DeleteAsync(
        EcnanibDbContext context,
        Guid userId,
        AccountTokenPurpose purpose,
        CancellationToken cancellationToken) =>
        context.Set<AccountToken>()
            .Where(row => row.UserId == userId && row.Purpose == purpose)
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Null for anything that isn't a token this could have issued, which costs no query. The length is
    /// checked first because it comes from the query string, and it sizes a buffer on the stack.
    /// </summary>
    private static byte[]? HashOf(string? token)
    {
        if (token is null || token.Length != Base64Url.GetEncodedLength(TokenLength))
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[TokenLength];

        if (!Base64Url.TryDecodeFromChars(token, bytes, out var written) || written != TokenLength)
        {
            return null;
        }

        return SHA256.HashData(bytes[..written]);
    }
}

/// <summary>
/// A token to put in a link. Not a record, whose ToString would print it, and the token is as good as
/// what it allows until it's used or expires.
/// </summary>
internal sealed class IssuedToken(string token, DateTimeOffset expiresAt)
{
    public string Token { get; } = token;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public override string ToString() => nameof(IssuedToken);
}

/// <summary>
/// Whose a used token was, and whether it came by email, which is what proves the address. The naming
/// convention reaches this too, so the query's columns are user_id and by_email.
/// </summary>
internal sealed class UsedToken
{
    public Guid UserId { get; init; }

    public bool ByEmail { get; init; }
}
