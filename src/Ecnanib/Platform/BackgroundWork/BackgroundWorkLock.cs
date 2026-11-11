using Ecnanib.Platform.Settings;
using Npgsql;

namespace Ecnanib.Platform.BackgroundWork;

/// <summary>
/// The database lock that makes one instance, and no other, run the background work: a
/// PostgreSQL advisory lock under <see cref="BackgroundJobs.LockKey"/> (NFR-ARC-4).
/// </summary>
/// <remarks>
/// <para>
/// The lock belongs to a database session, and this class keeps that session to itself for as
/// long as it holds the lock. The session does not come from the application's pool: closing it
/// ends it, and the lock with it. A pooled session would live on after it was given back, and
/// could keep a lock that nobody holds any more.
/// </para>
/// <para>
/// It is used by the scheduler alone, from one place at a time.
/// </para>
/// </remarks>
internal sealed class BackgroundWorkLock(ConnectionStringsSettings connectionStrings) : IAsyncDisposable
{
    // A question about the lock is answered at once or not at all. Without a limit, a database
    // that has stopped answering would keep the instance believing it holds the lock.
    private const int SecondsToAnswer = 5;

    private NpgsqlConnection? _session;

    /// <summary>
    /// Takes the lock if no other session holds it. It does not wait: the caller asks again.
    /// </summary>
    public async Task<bool> TryTakeAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            var session = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(connectionStrings.EcnanibDbContext.Reveal())
                {
                    Pooling = false,
                    CommandTimeout = SecondsToAnswer,
                }.ConnectionString);
            try
            {
                await session.OpenAsync(cancellationToken);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }

            _session = session;
        }

        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", _session);
        command.Parameters.AddWithValue("key", BackgroundJobs.LockKey);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Whether the session that took the lock still holds it, as the database sees it. It
    /// throws when the session has ended, which has released the lock.
    /// </summary>
    public async Task<bool> IsHeldAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return false;
        }

        // A lock taken with one 64-bit key shows as its two halves.
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory' AND granted AND pid = pg_backend_pid() AND objsubid = 1
                  AND classid::bigint = @high AND objid::bigint = @low)
            """,
            _session);
        command.Parameters.AddWithValue("high", BackgroundJobs.LockKey >> 32);
        command.Parameters.AddWithValue("low", BackgroundJobs.LockKey & 0xFFFFFFFF);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Gives the lock up by ending its session, whatever state the session is in. It does not
    /// throw, and does nothing when there is no session.
    /// </summary>
    public async Task ReleaseAsync()
    {
        var session = _session;
        _session = null;

        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The session was broken already, and its lock went with it.
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
