using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>The user a session belongs to, as far as a request needs to know them.</summary>
internal sealed record SignedInUser(Guid Id, string Email);

/// <summary>
/// The sessions in the database: starting one, finding the one a cookie names, and ending them.
/// A session ends by itself after the configured period of inactivity (NFR-SEC-1). See
/// docs/accounts.md.
/// </summary>
internal sealed class Sessions(
    SecuritySettings security,
    IDbContextFactory<EcnanibDbContext> contexts,
    TimeProvider clock,
    ILogger<Sessions> logger)
{
    private readonly TimeSpan _idle = TimeSpan.FromMinutes(security.SessionIdleMinutes);

    // Activity is not written on every request. A session whose stored time is this recent is
    // left as it is, so the stored time is never later than the last request, and a session
    // never lasts longer than configured. It is a sixtieth of the period, which is as many
    // seconds as the period has minutes, and a minute at most.
    private readonly TimeSpan _writeEvery = TimeSpan.FromSeconds(Math.Min(60, security.SessionIdleMinutes));

    /// <summary>
    /// Starts a session for a user whose password was just verified against
    /// <paramref name="verifiedPasswordHash"/>, and returns its token.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="verifiedPasswordHash">The stored hash that the password matched.</param>
    /// <param name="heldToken">The token of the session the browser holds now, which ends.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>Null when the password was changed since it was verified, so that it no longer signs in.</returns>
    public async Task<string?> StartAsync(
        Guid userId, string verifiedPasswordHash, string? heldToken, CancellationToken cancellationToken)
    {
        var (token, tokenHash) = SecretToken.Create();
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row is locked until the session is stored. Changing a password ends every session
        // of the account, so whichever comes first, a session never outlives the password it
        // was started with: either the change waits and then ends this session, or this waits
        // and then finds another hash.
        var unchanged = await context.Database
            .SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM users
                WHERE id = {userId} AND password_hash = {verifiedPasswordHash}
                FOR SHARE
                """)
            .ToListAsync(cancellationToken);

        if (unchanged.Count == 0)
        {
            return null;
        }

        // A browser has one session. The one it held ends, whoever it belonged to.
        if (heldToken is not null && SecretToken.HashOf(heldToken) is { } heldHash)
        {
            await context.Set<Session>()
                .Where(session => session.TokenHash == heldHash)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Sessions that ended by themselves, on browsers that never came back.
        var endedBefore = now - _idle;
        await context.Set<Session>()
            .Where(session => session.UserId == userId && session.LastActiveAt <= endedBefore)
            .ExecuteDeleteAsync(cancellationToken);

        context.Set<Session>().Add(new Session(Guid.CreateVersion7(), userId, tokenHash, now, now));
        await context.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return token;
    }

    /// <summary>
    /// The user of the session that a token names, when the session stands. Finding it counts as
    /// activity.
    /// </summary>
    /// <returns>Null when the text is not a token, or its session has ended.</returns>
    public async Task<SignedInUser?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (SecretToken.HashOf(token) is not { } tokenHash)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var found = await ReadAsync();
        if (found is null)
        {
            return null;
        }

        var inactiveFor = now - found.LastActiveAt;
        if (inactiveFor >= _idle)
        {
            // Only as it was read. A request that came just in time may have used the session
            // since, and then it stands.
            var ended = await context.Set<Session>()
                .Where(session => session.Id == found.Id && session.LastActiveAt == found.LastActiveAt)
                .ExecuteDeleteAsync(cancellationToken);

            if (ended == 1)
            {
                logger.LogInformation("A session of user {UserId} ended after inactivity", found.UserId);

                return null;
            }

            found = await ReadAsync();
            if (found is null || now - found.LastActiveAt >= _idle)
            {
                return null;
            }

            inactiveFor = now - found.LastActiveAt;
        }

        if (inactiveFor >= _writeEvery)
        {
            await context.Set<Session>()
                .Where(session => session.Id == found.Id && session.LastActiveAt < now)
                .ExecuteUpdateAsync(row => row.SetProperty(session => session.LastActiveAt, now), cancellationToken);
        }

        return new SignedInUser(found.UserId, found.Email);

        Task<FoundSession?> ReadAsync() =>
            context.Set<Session>()
                .Where(session => session.TokenHash == tokenHash)
                .Join(
                    context.Set<User>(),
                    session => session.UserId,
                    user => user.Id,
                    (session, user) => new FoundSession(session.Id, session.LastActiveAt, user.Id, user.Email))
                .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>A session as it was read, with the account it belongs to.</summary>
    private sealed record FoundSession(Guid Id, DateTimeOffset LastActiveAt, Guid UserId, string Email);

    /// <summary>Ends the session that a token names.</summary>
    /// <returns>The user it belonged to, or null when there was no such session.</returns>
    public async Task<Guid?> EndAsync(string token, CancellationToken cancellationToken)
    {
        if (SecretToken.HashOf(token) is not { } tokenHash)
        {
            return null;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var userId = await context.Set<Session>()
            .Where(session => session.TokenHash == tokenHash)
            .Select(session => (Guid?)session.UserId)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return null;
        }

        await context.Set<Session>()
            .Where(session => session.TokenHash == tokenHash)
            .ExecuteDeleteAsync(cancellationToken);

        return userId;
    }

    /// <summary>
    /// Ends every session of a user, inside the transaction of whoever changes their password:
    /// nobody stays signed in on a password that no longer is one.
    /// </summary>
    /// <returns>How many sessions ended.</returns>
    public static Task<int> EndAllAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        context.Set<Session>()
            .Where(session => session.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
}
