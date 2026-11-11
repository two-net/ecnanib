using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Ecnanib.Platform.Persistence;

/// <summary>Brings the database to the application's model when the application starts.</summary>
public static class DatabaseMigration
{
    /// <summary>
    /// The key of the PostgreSQL advisory lock that is held while migrations are applied. It is
    /// the text "Ecnanib" as a number, and it shows in <c>pg_locks</c> while an instance migrates.
    /// </summary>
    public const long LockKey = 0x45636E616E6962;

    /// <summary>
    /// Applies the migrations the database does not have yet, under a database lock, so that two
    /// instances starting together do not both migrate (NFR-DAT-3, NFR-ARC-4). Call it before
    /// the host starts: nothing is served or scheduled until it returns.
    /// </summary>
    /// <exception cref="DatabaseMigrationException">
    /// The database could not be reached or a migration failed, so the application must not start.
    /// </exception>
    public static void MigrateDatabase(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        host.Services.GetRequiredService<DatabaseMigrator>().Migrate();
    }
}

/// <summary>The database is not at the application's model, and could not be brought to it.</summary>
public sealed class DatabaseMigrationException(Exception cause)
    : Exception("The database could not be migrated, so Ecnanib does not start.", cause);

/// <summary>Applies pending migrations on one database session that holds the migration lock.</summary>
/// <remarks>
/// <para>
/// The lock and the migrations share a session, so the lock cannot be lost while the migrations
/// go on: when the session ends, both end. The object-relational mapper's own lock covers less,
/// because it is held only inside the transaction of a migration.
/// </para>
/// <para>
/// This is synchronous on purpose. It runs once, on the thread that starts the application,
/// before anything else does.
/// </para>
/// </remarks>
internal sealed class DatabaseMigrator(IDbContextFactory<EcnanibDbContext> contexts, ILogger<DatabaseMigrator> logger)
{
    private static readonly TimeSpan TimeBetweenTries = TimeSpan.FromMilliseconds(250);

    public void Migrate()
    {
        try
        {
            using var context = contexts.CreateDbContext();
            var database = context.Database;

            // A migration takes as long as it takes. It runs in one transaction, so stopping it
            // half-way would only mean doing all of it again.
            database.SetCommandTimeout(0);

            // Opened here, the connection stays open until it is closed here: everything below
            // runs on this one session.
            database.OpenConnection();
            try
            {
                var session = database.GetDbConnection();

                WaitForTheLock(session);

                var applied = false;
                try
                {
                    Apply(database);
                    applied = true;
                }
                finally
                {
                    Unlock(session, applied);
                }
            }
            finally
            {
                database.CloseConnection();
            }
        }
        catch (Exception error)
        {
            throw new DatabaseMigrationException(error);
        }
    }

    private void Apply(DatabaseFacade database)
    {
        var pending = database.GetPendingMigrations().ToList();

        if (pending.Count > 0)
        {
            logger.LogInformation(
                "Applying {MigrationCount} database migrations, from {FirstMigration} to {LastMigration}",
                pending.Count,
                pending[0],
                pending[^1]);
        }

        // Called even with nothing pending: it also refuses a model that is ahead of its migrations.
        database.Migrate();

        var current = database.GetAppliedMigrations().LastOrDefault();
        if (pending.Count > 0)
        {
            logger.LogInformation("The database was migrated to {Migration}", current);
        }
        else
        {
            logger.LogInformation("The database is up to date at {Migration}", current);
        }
    }

    /// <summary>
    /// Takes the lock, waiting for as long as another instance holds it. It asks again instead
    /// of waiting inside the database, so no time limit set on the connection can end the wait.
    /// </summary>
    private void WaitForTheLock(DbConnection session)
    {
        var waiting = false;

        while (!Scalar(session, "SELECT pg_try_advisory_lock(@key)"))
        {
            if (!waiting)
            {
                logger.LogInformation("Waiting for another instance to finish migrating the database");
                waiting = true;
            }

            Thread.Sleep(TimeBetweenTries);
        }
    }

    private void Unlock(DbConnection session, bool applied)
    {
        try
        {
            if (!Scalar(session, "SELECT pg_advisory_unlock(@key)"))
            {
                // The lock belongs to one session. A connection that is handed from session to
                // session between commands, as a pooler in transaction mode does, takes the
                // lock on one and asks another to release it.
                logger.LogWarning(
                    "The migration lock was not held by the session that released it, so the migrations may have run " +
                    "without it. Connect to the database directly, or through a pooler in session mode");
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException)
        {
            // The session has ended, and its lock went with it. After a failed migration, what
            // ended the session is the error that matters, and it is already on its way out.
            logger.Log(
                applied ? LogLevel.Warning : LogLevel.Debug,
                error,
                "The migration lock could not be released, because its session has ended");
        }
    }

    private static bool Scalar(DbConnection session, string sql)
    {
        using var command = session.CreateCommand();
        command.CommandText = sql;

        var key = command.CreateParameter();
        key.ParameterName = "key";
        key.Value = DatabaseMigration.LockKey;
        command.Parameters.Add(key);

        return command.ExecuteScalar() is true;
    }
}
