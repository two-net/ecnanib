using Ecnanib.Kernel;
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
    IAuditTrail audit,
    IdentitySettings settings,
    ILogger<Sessions> logger) : ISessions
{
    /// <summary>What audit records name as their target when they're about one session.</summary>
    public const string AuditTarget = "Session";

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

        if (!await EndAsync(userId, sessionId, AuditAction.SessionRevoked, cancellationToken))
        {
            return false;
        }

        logger.LogInformation("Revoked a session");

        return true;
    }

    public async Task<bool> SignOutAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { [LogProperties.UserId] = userId });

        if (!await EndAsync(userId, sessionId, AuditAction.SignedOut, cancellationToken))
        {
            return false;
        }

        logger.LogInformation("Signed out");

        return true;
    }

    /// <summary>
    /// Null when the password is no longer <paramref name="verifiedHash"/>. One statement, under a row
    /// lock, because a sign-in takes long enough to verify that a password change can finish during it,
    /// and a session started after that would escape the change. It takes FOR SHARE explicitly, because
    /// the lock the foreign key takes doesn't wait for an update to the hash. Takes the caller's
    /// context, so that the session commits with its audit record. In a transaction, the lock lasts
    /// until that commits.
    /// </summary>
    public async Task<Guid?> StartAsync(
        EcnanibDbContext context,
        Guid userId,
        string verifiedHash,
        ClientDetails client,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + settings.SessionAbsoluteTimeout;
        var ipAddress = ClientText.Address(client.Address);
        var userAgent = ClientText.UserAgent(client.UserAgent);

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

    /// <summary>
    /// Deletes the row and records why in one transaction, then closes the connections. The row goes
    /// first, so that a client reconnecting at once finds its cookie refused. False, changing nothing,
    /// when the session isn't this user's or has already ended.
    /// </summary>
    private async Task<bool> EndAsync(Guid userId, Guid sessionId, AuditAction action, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var ended = await context.Set<Session>()
            .Where(session => session.Id == sessionId && session.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        if (ended == 0)
        {
            return false;
        }

        audit.Add(context, AuditEntry.ByUser(action, userId) with { TargetType = AuditTarget, TargetId = sessionId.ToString() });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        connections.End([sessionId]);

        return true;
    }
}
