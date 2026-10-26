using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ecnanib.Database.Migrations;

/// <summary>
/// Applies pending migrations at startup (DB-02, NFR-ARCH-03). Migrations are forward-only: a database that knows
/// migrations this build does not is refused rather than reverted, and a model without a migration is refused
/// rather than silently mismatched. Every step is logged (FR-LOG-04); any failure is logged at Critical and thrown
/// as <see cref="DatabaseMigrationException"/> so the host never starts on a broken schema.
/// </summary>
internal sealed class DatabaseMigrator
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(IDbContextFactory<EcnanibDbContext> factory, TimeProvider time, ILogger<DatabaseMigrator> logger)
    {
        _factory = factory;
        _time = time;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var target = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
            var creator = db.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                // Same behaviour as EF's own Migrate(): a missing database is created (the role needs CREATEDB).
                _logger.LogInformation("Database {Database} on {Host} does not exist; creating it", target.Database, target.Host);
                await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
            }

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _logger.LogInformation("Connected to PostgreSQL {ServerVersion} database {Database} on {Host}",
                    connection.PostgreSqlVersion, target.Database, target.Host);
                RequireSupportedVersion(connection.PostgreSqlVersion);

                // Npgsql cannot tell whether the history table exists and probes it with a query that EF logs as an
                // error when it is missing; creating it idempotently first keeps a first start free of spurious errors.
                await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

                var known = db.Database.GetMigrations().ToList();
                var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
                if (unknown.Count > 0)
                {
                    throw new DatabaseMigrationException(
                        $"The database has {unknown.Count} migration(s) this build does not know: {string.Join(", ", unknown)}. " +
                        "Migrations are forward-only (DB-02); run a build that includes them.");
                }

                if (db.Database.HasPendingModelChanges())
                {
                    throw new DatabaseMigrationException(
                        "The data model has changes that no migration covers. Add a migration before starting (DB-02).");
                }

                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                if (pending.Count == 0)
                {
                    _logger.LogInformation("Database schema is up to date ({Count} migrations applied)", applied.Count);
                    return;
                }

                _logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                foreach (var migration in pending)
                {
                    var started = _time.GetTimestamp();
                    try
                    {
                        await db.Database.MigrateAsync(migration, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new DatabaseMigrationException($"Migration '{migration}' failed: {Describe(ex)}", ex);
                    }

                    _logger.LogInformation("Applied database migration {Migration} in {ElapsedMs:F0} ms",
                        migration, _time.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var wrapped = ex as DatabaseMigrationException
                ?? new DatabaseMigrationException($"Database migration failed: {Describe(ex)}", ex);
            _logger.LogCritical(wrapped, "Database migration failed; the application will not start (NFR-ARCH-03): {Reason}", wrapped.Message);
            throw wrapped;
        }
    }

    /// <summary>
    /// The cause as the operator should read it on the Critical line: every distinct message down the inner-exception
    /// chain, so a refused connection is named ("Failed to connect to host:port: Connection refused") and not only EF's
    /// "likely due to a transient failure" wrapper around it. A wrapper that already quotes its cause, such as an
    /// <see cref="AggregateException"/>'s "One or more errors occurred. (…)", adds nothing and is left out.
    /// </summary>
    internal static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.TrimEnd('.', ' ');
            if (message.Length > 0 && !messages.Contains(message, StringComparer.Ordinal))
                messages.Add(message);
        }

        var own = messages.Where((message, index) => !messages.Skip(index + 1).Any(inner => message.Contains(inner, StringComparison.Ordinal)));
        return string.Join(": ", own);
    }

    /// <summary>Rejects servers older than <see cref="NpgsqlConfiguration.MinimumPostgresVersion"/> before any DDL runs.</summary>
    internal static void RequireSupportedVersion(Version serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        if (serverVersion < NpgsqlConfiguration.MinimumPostgresVersion)
        {
            throw new DatabaseMigrationException(
                $"PostgreSQL {NpgsqlConfiguration.MinimumPostgresVersion} or newer is required; the server reports {serverVersion}.");
        }
    }
}
