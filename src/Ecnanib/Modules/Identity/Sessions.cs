using System.Net;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// The rows behind FR-AUTH-07. HTTP asks here on every request, through the cookie's validation, and
/// a circuit through <see cref="SessionHubFilter"/>. See Sessions in docs/architecture.md.
/// </summary>
internal sealed class Sessions(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    SessionConnections connections,
    IdentitySettings settings,
    ILogger<Sessions> logger) : ISessions
{
    public async Task<IReadOnlyList<SessionRecord>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var idleSince = now - settings.SessionIdleTimeout;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<Session>()
            .Where(session => session.UserId == userId && session.ExpiresAt > now && session.LastSeenAt > idleSince)
            .OrderByDescending(session => session.LastSeenAt)
            .Select(session => new SessionRecord(session.Id, session.CreatedAt, session.LastSeenAt, session.IpAddress, session.UserAgent))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var revoked = await context.Set<Session>()
            .Where(session => session.Id == sessionId && session.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        if (revoked == 0)
        {
            return false;
        }

        connections.End([sessionId]);
        logger.LogInformation("Revoked a session");

        return true;
    }

    /// <summary>
    /// Null when the password is no longer <paramref name="verifiedHash"/>. One statement, under a row
    /// lock, because a sign-in takes long enough to verify that a password change can finish during it,
    /// and a session started after that would escape the change. It takes FOR SHARE explicitly, because
    /// the lock the foreign key takes doesn't wait for an update to the hash.
    /// </summary>
    public async Task<Guid?> StartAsync(Guid userId, string verifiedHash, ClientDetails client, CancellationToken cancellationToken)
    {
        var sessionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + settings.SessionAbsoluteTimeout;
        var ipAddress = AddressOf(client.Address);
        var userAgent = Clean(client.UserAgent);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var started = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO sessions (id, user_id, created_at, last_seen_at, expires_at, ip_address, user_agent)
            SELECT {sessionId}, id, {now}, {now}, {expiresAt}, {ipAddress}::text, {userAgent}::text
            FROM users
            WHERE id = {userId} AND password_hash = {verifiedHash}
            FOR SHARE
            """,
            cancellationToken);

        return started > 0 ? sessionId : null;
    }

    /// <summary>
    /// Whether the session is still alive. <paramref name="activity"/> says the user did something, so
    /// it counts against the idle timeout, which writes at most once per touch interval.
    /// </summary>
    public async Task<bool> SeenAsync(Guid sessionId, bool activity, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var idleSince = now - settings.SessionIdleTimeout;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var lastSeen = await context.Set<Session>()
            .Where(session => session.Id == sessionId && session.ExpiresAt > now && session.LastSeenAt > idleSince)
            .Select(session => (DateTimeOffset?)session.LastSeenAt)
            .SingleOrDefaultAsync(cancellationToken);

        if (lastSeen is null)
        {
            return false;
        }

        if (activity && lastSeen <= now - settings.SessionTouchInterval)
        {
            await context.Set<Session>()
                .Where(session => session.Id == sessionId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.LastSeenAt, now), cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Takes the caller's context, so that this commits with the new password. Pass what it returns
    /// to <see cref="EndConnections"/> once that has committed.
    /// </summary>
    public Task<List<Guid>> DeleteOthersAsync(EcnanibDbContext context, Guid userId, Guid keptSessionId, CancellationToken cancellationToken) =>
        context.Database
            .SqlQuery<Guid>($"""DELETE FROM sessions WHERE user_id = {userId} AND id <> {keptSessionId} RETURNING id AS "Value" """)
            .ToListAsync(cancellationToken);

    public void EndConnections(IEnumerable<Guid> sessionIds) => connections.End(sessionIds);

    /// <summary>Deletes sessions past either timeout and closes their connections. Returns how many.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var idleSince = now - settings.SessionIdleTimeout;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var expired = await context.Database
            .SqlQuery<Guid>($"""DELETE FROM sessions WHERE expires_at <= {now} OR last_seen_at <= {idleSince} RETURNING id AS "Value" """)
            .ToListAsync(cancellationToken);

        connections.End(expired);

        return expired.Count;
    }

    /// <summary>A dual-stack socket reports IPv4 clients in mapped form, which nobody would recognise.</summary>
    private static string? AddressOf(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString();

    /// <summary>
    /// A client writes this header, so it has to fit and be storable: PostgreSQL refuses a NUL, and half
    /// a surrogate pair can't be encoded. Either would be a 500 and a database row a visitor chose to write.
    /// </summary>
    private static string? Clean(string? userAgent)
    {
        var text = new string([.. (userAgent ?? string.Empty).Where(character => !char.IsControl(character))]).Trim();

        if (text.Length == 0)
        {
            return null;
        }

        var length = Math.Min(text.Length, Session.UserAgentMaxLength);

        if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}
