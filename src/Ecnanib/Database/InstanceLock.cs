using Ecnanib.Database.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ecnanib.Database;

/// <summary>
/// NFR-REL-04: exactly one Ecnanib process per database. A PostgreSQL session advisory lock on <see cref="Key"/> is taken
/// through one context whose connection stays open for the process's lifetime (DB-01: the ORM is the only way in), so
/// the lock goes when the process does, cleanly or not. <see cref="AcquireAsync"/> runs from <c>Program</c> after the
/// migrations and before the host starts: a second instance waits <c>Database:InstanceLockWaitSeconds</c> for the
/// previous one to release the lock (a restart), then fails with <see cref="InstanceLockException"/> after a Critical
/// log naming the backend that holds it. As a hosted service it keeps the connection alive, takes the lock again when
/// the connection was lost, and stops the host when another instance took it meanwhile.
/// </summary>
internal sealed class InstanceLock : IHostedService, IInstanceLockStatus, IAsyncDisposable, IDisposable
{
    /// <summary>"ecnanib" in ASCII, 28538298379954530: the one advisory lock key of the application, per database.</summary>
    internal const long Key = 0x65636E616E6962;

    /// <summary>PostgreSQL files a 64-bit advisory key as its high and low 32 bits (classid and objid).</summary>
    private const int KeyHigh = (int)(Key >> 32);

    private const int KeyLow = (int)(Key & 0xFFFFFFFF);

    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<DatabaseOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InstanceLock> _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly Lock _statusLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private EcnanibDbContext? _db;
    private Task? _keepAlive;
    private InstanceLockStatus _status = InstanceLockStatus.NotHeld;
    private bool _disposed;

    public InstanceLock(IDbContextFactory<EcnanibDbContext> factory, IOptions<DatabaseOptions> options, TimeProvider time, ILogger<InstanceLock> logger, IHostApplicationLifetime? lifetime = null)
    {
        _factory = factory;
        _options = options;
        _time = time;
        _logger = logger;
        _lifetime = lifetime;
    }

    public InstanceLockStatus GetStatus()
    {
        lock (_statusLock)
            return _status;
    }

    /// <summary>Takes the lock, waiting up to <c>Database:InstanceLockWaitSeconds</c> for a previous instance to release it; a no-op when this process holds it.</summary>
    public async Task AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (_db is not null)
            return;
        var (database, host) = Target();
        var started = _time.GetUtcNow();
        var waiting = false;
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                if (await TryLockAsync(db, cancellationToken).ConfigureAwait(false))
                {
                    var pid = await BackendPidAsync(db, cancellationToken).ConfigureAwait(false);
                    _db = db;
                    Set(new InstanceLockStatus(InstanceLockState.Held, _time.GetUtcNow(), pid, _time.GetUtcNow(), null, null));
                    _logger.LogInformation("Instance lock on database {Database} on {Host} acquired as backend {Pid} (NFR-REL-04)", database, host, pid);
                    return;
                }

                if (_time.GetUtcNow() - started >= _options.Value.InstanceLockWait)
                {
                    var holder = await HolderPidAsync(db, cancellationToken).ConfigureAwait(false);
                    var message = $"Another Ecnanib instance holds database {database} on {host} (backend {holder?.ToString() ?? "unknown"}); this instance will not start (NFR-REL-04). Stop it, or point this instance at another database.";
                    _logger.LogCritical("{Message}", message);
                    throw new InstanceLockException(message);
                }

                if (!waiting)
                {
                    waiting = true;
                    _logger.LogWarning("Database {Database} on {Host} is locked by another Ecnanib instance; waiting up to {Wait} for it to release the lock (NFR-REL-04)", database, host, _options.Value.InstanceLockWait);
                }

                await Task.Delay(RetryInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await AcquireAsync(cancellationToken).ConfigureAwait(false);
        _keepAlive = KeepAliveAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_disposed)
            await _stopping.CancelAsync().ConfigureAwait(false);
        if (_keepAlive is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await ReleaseAsync().ConfigureAwait(false);
    }

    /// <summary>Idempotent: the container disposes the singleton once as itself and once as the hosted service.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await ReleaseAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>For a container disposed synchronously (the test hosts); the application disposes asynchronously.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopping.Cancel();
        if (_db is { } db)
        {
            _db = null;
            db.Dispose();
            Set(InstanceLockStatus.NotHeld);
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// Every <see cref="KeepAliveInterval"/> the holding session is asked whether it still holds the lock: a connection
    /// the retrying strategy or the pool replaced under the context answers a plain query and holds nothing, so the probe
    /// reads <c>pg_locks</c> for this backend. A lost lock is taken again on a new connection, or the host stopped when
    /// another instance holds it by now.
    /// </summary>
    private async Task KeepAliveAsync(CancellationToken stoppingToken)
    {
        var (database, host) = Target();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(KeepAliveInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            string? why;
            try
            {
                if (_db is { } db && GetStatus().State == InstanceLockState.Held)
                {
                    if (await HoldsAsync(db, stoppingToken).ConfigureAwait(false))
                    {
                        Set(GetStatus() with { LastCheckedAt = _time.GetUtcNow() });
                        continue;
                    }

                    why = "the session no longer holds the lock";
                }
                else
                {
                    why = GetStatus().LastError ?? "the lock is not held";
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                why = ex.Message;
            }

            Set(GetStatus() with { State = InstanceLockState.Reacquiring, LastError = why, LastErrorAt = _time.GetUtcNow() });
            _logger.LogWarning("The instance lock on database {Database} on {Host} was lost ({Reason}); it is taken again (NFR-REL-04)", database, host, why);
            if (_db is { } lost)
            {
                _db = null;
                await lost.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                var db = await _factory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await db.Database.OpenConnectionAsync(stoppingToken).ConfigureAwait(false);
                    if (await TryLockAsync(db, stoppingToken).ConfigureAwait(false))
                    {
                        var pid = await BackendPidAsync(db, stoppingToken).ConfigureAwait(false);
                        _db = db;
                        Set(GetStatus() with { State = InstanceLockState.Held, BackendPid = pid, LastCheckedAt = _time.GetUtcNow() });
                        _logger.LogWarning("Instance lock on database {Database} on {Host} re-acquired as backend {Pid} (NFR-REL-04)", database, host, pid);
                        continue;
                    }

                    var holder = await HolderPidAsync(db, stoppingToken).ConfigureAwait(false);
                    await db.DisposeAsync().ConfigureAwait(false);
                    Set(GetStatus() with { State = InstanceLockState.Lost, LastError = $"another instance (backend {holder?.ToString() ?? "unknown"}) holds the lock", LastErrorAt = _time.GetUtcNow() });
                    _logger.LogCritical("Another Ecnanib instance (backend {Pid}) took the lock on database {Database} on {Host} while this one had lost its connection; this instance stops (NFR-REL-04)", holder, database, host);
                    _lifetime?.StopApplication();
                    return;
                }
                catch
                {
                    await db.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Set(GetStatus() with { LastError = ex.Message, LastErrorAt = _time.GetUtcNow() });
                _logger.LogWarning(ex, "The instance lock on database {Database} on {Host} could not be taken again yet; next attempt in {Interval}", database, host, KeepAliveInterval);
            }
        }
    }

    private async Task ReleaseAsync()
    {
        if (_db is not { } db)
            return;
        _db = null;
        // Returning the connection to the pool resets the session, which releases the advisory lock.
        await db.DisposeAsync().ConfigureAwait(false);
        Set(InstanceLockStatus.NotHeld);
        _logger.LogInformation("Instance lock released (NFR-REL-04)");
    }

    private static Task<bool> TryLockAsync(EcnanibDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock({0}) AS \"Value\"", Key).SingleAsync(cancellationToken);

    /// <summary>Whether the backend this context talks to holds the key, which a replaced connection does not.</summary>
    private static Task<bool> HoldsAsync(EcnanibDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND pid = pg_backend_pid() AND classid = {0} AND objid = {1} AND database = (SELECT oid FROM pg_database WHERE datname = current_database())) AS \"Value\"",
            KeyHigh, KeyLow).SingleAsync(cancellationToken);

    private static Task<int> BackendPidAsync(EcnanibDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(cancellationToken);

    /// <summary>The backend holding the key in this database; PostgreSQL files a 64-bit advisory key as its high and low 32 bits.</summary>
    private static async Task<int?> HolderPidAsync(EcnanibDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var pids = await db.Database.SqlQueryRaw<int>(
                "SELECT pid AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND classid = {0} AND objid = {1} AND database = (SELECT oid FROM pg_database WHERE datname = current_database())",
                KeyHigh, KeyLow).ToListAsync(cancellationToken).ConfigureAwait(false);
            return pids.Count == 0 ? null : pids[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private (string? Database, string? Host) Target()
    {
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(_options.Value.ConnectionString);
            return (csb.Database, csb.Host);
        }
        catch (ArgumentException)
        {
            return (null, null);
        }
    }

    private void Set(InstanceLockStatus status)
    {
        lock (_statusLock)
            _status = status;
    }
}
