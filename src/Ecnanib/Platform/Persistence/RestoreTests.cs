using System.Text;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Tests that a backup brings the database back: it restores the latest backup to a database of
/// its own, holds that database to what the backup says of itself, and records how the test
/// ended (NFR-DAT-11). See docs/backups.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The backup is restored to a scratch database of the same server,</b> which is named after
/// the database of the application, with <see cref="ScratchSuffix"/> behind its name. The test
/// makes it and drops it, so the role of the application needs the right to create a database.
/// </para>
/// <para>
/// <b>The database of the application is only read.</b> The one database that is ever dropped
/// here is the one that this class names: a name that the server would cut short is refused,
/// and nobody who is connected to a database is put out to drop it.
/// </para>
/// <para>
/// <b>Every test that ends is recorded,</b> as a row of <c>restore_tests</c>, and a test that
/// failed then fails the run of its job. A test that was stopped is no test, and records nothing.
/// </para>
/// </remarks>
internal sealed class RestoreTests(
    DatabaseBackups backups,
    BackupSettings settings,
    ConnectionStringsSettings connection,
    NpgsqlDataSource database,
    PostgresTools tools,
    IDbContextFactory<EcnanibDbContext> contexts,
    TimeProvider clock,
    ILogger<RestoreTests> logger)
{
    /// <summary>What follows the name of the application's database in the name of the scratch database.</summary>
    public const string ScratchSuffix = "_restore_test";

    // The longest name that PostgreSQL keeps whole, in bytes. It cuts a longer one short.
    private const int LongestName = 63;

    // How many differences an error names. One says that the backup is not to be relied on.
    private const int MostDifferences = 5;

    /// <summary>
    /// Tests the restore of the latest backup, and of one that it takes itself when there is
    /// none yet. It returns when the backup holds what it says.
    /// </summary>
    /// <exception cref="RestoreTestFailedException">The test failed. It is recorded.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backup = await backups.LatestAsync(cancellationToken)
            ?? await backups.TakeAsync(BackupReason.RestoreTest, cancellationToken);

        var startedAt = clock.GetUtcNow();
        DatabaseContents? restored = null;
        RestoreTestFailure? failure = null;
        Exception? cause = null;

        try
        {
            restored = await RestoreAsync(backup, cancellationToken);

            if (Differences(backup.Contents, restored) is { Count: > 0 } differences)
            {
                failure = RestoreTestFailure.Differs;
                cause = new InvalidOperationException(
                    "The database that came of the backup is not what the backup says: " + string.Join("; ", differences) + ".");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = RestoreTestFailure.NotRestored;
            cause = error;
        }

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            context.Set<RestoreTest>().Add(new RestoreTest(
                Guid.CreateVersion7(),
                backup.Name,
                backup.Manifest.TakenAt,
                startedAt,
                clock.GetUtcNow(),
                failure is null ? RestoreTestResult.Succeeded : RestoreTestResult.Failed,
                failure,
                restored?.Tables.Count ?? 0,
                restored?.Rows ?? 0));

            await context.SaveChangesAsync(cancellationToken);
        }

        if (cause is not null)
        {
            throw new RestoreTestFailedException(backup.Name, cause);
        }

        logger.LogInformation(
            "The backup {Backup} was restored to a database of its own, which holds what the backup says: {Tables} tables, {Rows} rows",
            backup.Name,
            restored!.Tables.Count,
            restored.Rows);
    }

    /// <summary>
    /// Restores a backup to the scratch database, reads what that database holds, and drops it
    /// again, however the restore ended.
    /// </summary>
    private async Task<DatabaseContents> RestoreAsync(DatabaseBackup backup, CancellationToken cancellationToken)
    {
        var (scratch, quoted) = await ScratchAsync(cancellationToken);

        // One that an earlier test left behind, when its process ended before it was done.
        await ExecuteAsync($"DROP DATABASE IF EXISTS {quoted}", cancellationToken);
        await ExecuteAsync($"CREATE DATABASE {quoted}", cancellationToken);

        try
        {
            await using (var file = DatabaseBackups.Open(backup))
            {
                await tools.RunAsync(
                    settings.PgRestore,
                    ["--no-owner", "--no-privileges", "--exit-on-error", $"--dbname={scratch}"],
                    input: file,
                    output: null,
                    cancellationToken);
            }

            // A session of its own, and not one of a pool: it must be gone before the database is dropped.
            await using var session = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(connection.EcnanibDbContext.Reveal())
                {
                    Database = scratch,
                    Pooling = false,
                }.ConnectionString);
            await session.OpenAsync(cancellationToken);

            return await DatabaseContents.ReadAsync(session, cancellationToken);
        }
        finally
        {
            await DropAsync(scratch, quoted);
        }
    }

    /// <summary>The name of the scratch database, plain and quoted as an identifier, both as the server says them.</summary>
    private async Task<(string Name, string Quoted)> ScratchAsync(CancellationToken cancellationToken)
    {
        await using var ask = database.CreateCommand("SELECT current_database() || @suffix, quote_ident(current_database() || @suffix)");
        ask.Parameters.AddWithValue("suffix", ScratchSuffix);

        await using var answer = await ask.ExecuteReaderAsync(cancellationToken);
        await answer.ReadAsync(cancellationToken);

        var name = answer.GetString(0);

        if (Encoding.UTF8.GetByteCount(name) > LongestName)
        {
            // The server would cut the name short, to a name that is not this one.
            throw new InvalidOperationException(
                $"The name of the database is too long for the database that a restore is tested in: with {ScratchSuffix} behind it, it has more than {LongestName} bytes.");
        }

        return (name, answer.GetString(1));
    }

    /// <summary>Drops the scratch database. A test that could not drop it has still ended as it ended.</summary>
    private async Task DropAsync(string scratch, string quoted)
    {
        try
        {
            // Not stopped with the test: a test that is stopped leaves no database behind either.
            await ExecuteAsync($"DROP DATABASE IF EXISTS {quoted}", CancellationToken.None);
        }
        catch (NpgsqlException error)
        {
            logger.LogWarning(
                error,
                "The database {Database} that a restore was tested in could not be dropped. The next test drops it first",
                scratch);
        }
    }

    /// <summary>
    /// Sends a statement that makes or drops the scratch database. Its name cannot be a
    /// parameter: it is no text of a request, and the server quoted it as an identifier.
    /// </summary>
    private async Task ExecuteAsync(string statement, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(statement);

        // Making a database copies one, and takes as long as it takes.
        command.CommandTimeout = 0;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>What the restored database holds that the backup does not say, each in a few words, and the first few of them.</summary>
    private static List<string> Differences(DatabaseContents said, DatabaseContents restored)
    {
        var differences = new List<string>();

        if (said.Migration != restored.Migration)
        {
            differences.Add($"it is at the migration {restored.Migration ?? "(none)"} in place of {said.Migration ?? "(none)"}");
        }

        foreach (var (table, rows) in said.Tables)
        {
            if (!restored.Tables.TryGetValue(table, out var found))
            {
                differences.Add($"the table {table} is missing");
            }
            else if (found != rows)
            {
                differences.Add($"the table {table} holds {found} rows in place of {rows}");
            }
        }

        differences.AddRange(
            restored.Tables.Keys.Where(table => !said.Tables.ContainsKey(table)).Select(table => $"the table {table} is not of the backup"));

        return [.. differences.Take(MostDifferences)];
    }
}

/// <summary>A backup could not be relied on to bring the database back.</summary>
internal sealed class RestoreTestFailedException(string backup, Exception cause)
    : Exception($"The restore of the backup {backup} was tested, and failed.", cause);

/// <summary>
/// Tests a restore on its schedule, every <c>Backup:RestoreTestDays</c> days (NFR-DAT-11). See
/// <see cref="RestoreTests"/> and docs/backups.md.
/// </summary>
/// <remarks>
/// A test that failed fails its run: the scheduler logs the error, and the status of the job
/// says that its latest run failed.
/// </remarks>
internal sealed class RestoreTestJob(RestoreTests tests, BackupSettings settings) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "restore-test";

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(TimeSpan.FromDays(settings.RestoreTestDays));

    public Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        return tests.RunAsync(cancellationToken);
    }
}
