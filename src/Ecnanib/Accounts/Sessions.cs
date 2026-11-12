using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>The user a session belongs to, as far as a request needs to know them.</summary>
/// <param name="Id">The id of the account.</param>
/// <param name="Email">The address of the account, as it is stored.</param>
/// <param name="IsVerified">Whether that address is verified. Only then does it say who the user is.</param>
internal sealed record SessionUser(Guid Id, string Email, bool IsVerified);

/// <summary>
/// What giving the right password led to: a session, or a sign-in that waits for a code. Neither
/// is set when the password was changed since it was verified, so that it no longer signs in.
/// </summary>
/// <remarks>It holds a token, which is a secret, so it is not a record: nothing prints its members.</remarks>
internal readonly struct SignInStart
{
    /// <summary>The token of the session that started, for an account without two-factor authentication.</summary>
    public string? SessionToken { get; init; }

    /// <summary>The token of the sign-in that waits for a code, for an account with it.</summary>
    public string? PendingToken { get; init; }
}

/// <summary>A sign-in that waits for a code, as far as the page that takes the code needs to know it.</summary>
internal sealed record WaitingSignIn(Guid Id, Guid UserId);

/// <summary>
/// The sessions in the database: starting one, finding the one a cookie names, and ending them.
/// A session ends by itself after the configured period of inactivity (NFR-SEC-1). The sign-ins
/// that wait for a code are here too, because the same lock decides which of the two a password
/// starts (FR-ACC-3). A session also holds when its user last proved who they are again, which
/// therefore ends with the session (FR-ACC-4). See docs/accounts.md.
/// </summary>
/// <remarks>
/// What is asked for a user is asked with <see cref="Ownership.OwnedBy{TEntity}"/>. A session
/// and a sign-in that waits are also found by the hash of the token their browser holds, with
/// no user named: that is how a request comes to be signed in, and the token is the proof. See
/// docs/ownership.md.
/// </remarks>
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
    /// Starts what the right password starts, for a user whose password was just verified against
    /// <paramref name="verifiedPasswordHash"/>: a session, or for an account with two-factor
    /// authentication a sign-in that waits for a code.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="verifiedPasswordHash">The stored hash that the password matched.</param>
    /// <param name="heldToken">The token of the session the browser holds now, which ends when another starts.</param>
    /// <param name="heldPendingToken">The token of the sign-in the browser has waiting for a code, which ends.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public async Task<SignInStart> StartAsync(
        Guid userId,
        string verifiedPasswordHash,
        string? heldToken,
        string? heldPendingToken,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row is locked until the session is stored. Changing a password ends every session
        // of the account, so whichever comes first, a session never outlives the password it
        // was started with: either the change waits and then ends this session, or this waits
        // and then finds another hash.
        //
        // The same lock, in the same statement, says whether the account asks for a code.
        // Turning two-factor authentication on changes the row, so it waits for this or this
        // waits for it, and a password alone never starts a session of an account that has it on.
        var asksForCode = await context.Database
            .SqlQuery<bool>(
                $"""
                SELECT two_factor_enabled_at IS NOT NULL AS "Value" FROM users
                WHERE id = {userId} AND password_hash = {verifiedPasswordHash}
                FOR SHARE
                """)
            .ToListAsync(cancellationToken);

        if (asksForCode.Count == 0)
        {
            return default;
        }

        // A browser has one sign-in waiting for a code. The one it held ends, whoever it belonged to.
        if (heldPendingToken is not null && SecretToken.HashOf(heldPendingToken) is { } heldPendingHash)
        {
            await context.Set<PendingSignIn>()
                .Where(pending => pending.TokenHash == heldPendingHash)
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Those of the account that nobody gave a code for in time. One that another request is
        // ending at this moment is left to it. This lock is shared, so two passwords of one
        // account can be here at once, each having ended a sign-in that the other would clear
        // away: if either waited for the other, both would wait for ever.
        await context.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM pending_sign_ins
            WHERE id IN (
                SELECT id FROM pending_sign_ins
                WHERE user_id = {userId} AND expires_at <= {now}
                FOR UPDATE SKIP LOCKED)
            """,
            cancellationToken);

        SignInStart started;
        if (asksForCode[0])
        {
            var (token, tokenHash) = SecretToken.Create();

            context.Set<PendingSignIn>().Add(
                new PendingSignIn(Guid.CreateVersion7(), userId, tokenHash, now + PendingSignIn.Lifetime));
            await context.SaveChangesAsync(cancellationToken);

            started = new SignInStart { PendingToken = token };
        }
        else
        {
            started = new SignInStart { SessionToken = await AddAsync(context, userId, heldToken, now, cancellationToken) };
        }

        await transaction.CommitAsync(cancellationToken);

        return started;
    }

    /// <summary>
    /// Stores a session for a user inside the transaction of the caller, who holds the lock on
    /// the user's row and has made sure that the user may be signed in. Returns its token.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user.</param>
    /// <param name="heldToken">The token of the session the browser holds now, which ends.</param>
    /// <param name="now">The time the session starts at.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public async Task<string> AddAsync(
        EcnanibDbContext context, Guid userId, string? heldToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (token, tokenHash) = SecretToken.Create();

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
            .OwnedBy(userId)
            .Where(session => session.LastActiveAt <= endedBefore)
            .ExecuteDeleteAsync(cancellationToken);

        // Signing in is not proving it again: a session starts without a re-authentication.
        context.Set<Session>().Add(new Session(Guid.CreateVersion7(), userId, tokenHash, now, now, reauthenticatedAt: null));
        await context.SaveChangesAsync(cancellationToken);

        return token;
    }

    /// <summary>
    /// When the user of a session last proved who they are again on its browser (FR-ACC-4), as
    /// the database says at this moment.
    /// </summary>
    /// <param name="token">The token of the session.</param>
    /// <param name="userId">The user the request is signed in as, whose session it must be.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>
    /// Null when they have not since they signed in, when the text is not a token, or when the
    /// session has ended or is not that user's.
    /// </returns>
    public async Task<DateTimeOffset?> ReauthenticatedAtAsync(string token, Guid userId, CancellationToken cancellationToken)
    {
        if (SecretToken.HashOf(token) is not { } tokenHash)
        {
            return null;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Session>()
            .OwnedBy(userId)
            .Where(session => session.TokenHash == tokenHash)
            .Select(session => session.ReauthenticatedAt)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Writes down that the user of a session proved who they are again, inside the transaction
    /// of whoever took the proof. Only the session of the browser that gave it is marked.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="token">The token of the session.</param>
    /// <param name="userId">The user who gave the proof, whose session it must be.</param>
    /// <param name="now">The time the proof was given at.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>False when the text is not a token, or the session has ended or is not that user's.</returns>
    public static async Task<bool> MarkReauthenticatedAsync(
        EcnanibDbContext context, string token, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (SecretToken.HashOf(token) is not { } tokenHash)
        {
            return false;
        }

        var marked = await context.Set<Session>()
            .OwnedBy(userId)
            .Where(session => session.TokenHash == tokenHash)
            .ExecuteUpdateAsync(
                row => row.SetProperty(session => session.ReauthenticatedAt, (DateTimeOffset?)now), cancellationToken);

        return marked == 1;
    }

    /// <summary>
    /// The user of the session that a token names, when the session stands. Finding it counts as
    /// activity.
    /// </summary>
    /// <returns>Null when the text is not a token, or its session has ended.</returns>
    public async Task<SessionUser?> FindAsync(string token, CancellationToken cancellationToken)
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

        return new SessionUser(found.UserId, found.Email, found.IsVerified);

        Task<FoundSession?> ReadAsync() =>
            context.Set<Session>()
                .Where(session => session.TokenHash == tokenHash)
                .Join(
                    context.Set<User>(),
                    session => session.UserId,
                    user => user.Id,
                    (session, user) => new FoundSession(
                        session.Id, session.LastActiveAt, user.Id, user.Email, user.EmailVerifiedAt != null))
                .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>A session as it was read, with the account it belongs to.</summary>
    private sealed record FoundSession(Guid Id, DateTimeOffset LastActiveAt, Guid UserId, string Email, bool IsVerified);

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
    /// nobody stays signed in on a password that no longer is one. A sign-in that waits for a
    /// code ends with them, because it was started with that password.
    /// </summary>
    /// <returns>How many sessions ended.</returns>
    public static async Task<int> EndAllAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        await EndWaitingAsync(context, userId, cancellationToken);

        return await context.Set<Session>()
            .OwnedBy(userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Ends every session of a user but the one of the browser that asks for it, inside the
    /// transaction of whoever turns two-factor authentication on: the others were started with
    /// the password alone. Every sign-in that waits for a code ends too.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user.</param>
    /// <param name="keptToken">The token of the session that stays.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>How many sessions ended.</returns>
    public static async Task<int> EndOthersAsync(
        EcnanibDbContext context, Guid userId, string? keptToken, CancellationToken cancellationToken)
    {
        await EndWaitingAsync(context, userId, cancellationToken);

        var others = context.Set<Session>().OwnedBy(userId);
        if (keptToken is not null && SecretToken.HashOf(keptToken) is { } keptHash)
        {
            others = others.Where(session => session.TokenHash != keptHash);
        }

        return await others.ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The sign-in that a token names, when it still waits for its code. One that waited too
    /// long is not found.
    /// </summary>
    /// <returns>Null when the text is not a token, or names no sign-in that waits.</returns>
    public async Task<WaitingSignIn?> FindWaitingAsync(string token, CancellationToken cancellationToken)
    {
        if (SecretToken.HashOf(token) is not { } tokenHash)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<PendingSignIn>()
            .Where(pending => pending.TokenHash == tokenHash && pending.ExpiresAt > now)
            .Select(pending => new WaitingSignIn(pending.Id, pending.UserId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>Ends the sign-in that a token names, when a browser gives it up.</summary>
    public async Task EndWaitingAsync(string token, CancellationToken cancellationToken)
    {
        if (SecretToken.HashOf(token) is not { } tokenHash)
        {
            return;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        await context.Set<PendingSignIn>()
            .Where(pending => pending.TokenHash == tokenHash)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Ends every sign-in of a user that waits for a code, inside the transaction of whoever
    /// changes what such a sign-in was started under.
    /// </summary>
    public static Task<int> EndWaitingAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken) =>
        context.Set<PendingSignIn>()
            .OwnedBy(userId)
            .ExecuteDeleteAsync(cancellationToken);
}
