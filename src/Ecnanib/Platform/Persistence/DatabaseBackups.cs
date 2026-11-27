using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Settings;
using Npgsql;

namespace Ecnanib.Platform.Persistence;

/// <summary>Why a backup was taken, which its name says.</summary>
internal static class BackupReason
{
    /// <summary>The job took it, on its schedule.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>A restore was to be tested, and there was no backup to test it with.</summary>
    public const string RestoreTest = "restore-test";

    /// <summary>A migration that is marked as destructive was about to be applied.</summary>
    public static string Before(string migration) => $"before-{migration}";
}

/// <summary>
/// What a database holds, as far as a backup is held to it: the migration that it is at, and
/// how many rows each of its tables has.
/// </summary>
/// <param name="Migration">The latest migration that the database records as applied, or null when it records none.</param>
/// <param name="Tables">The rows of each ordinary table of the schema, by the name of the table.</param>
internal sealed record DatabaseContents(string? Migration, IReadOnlyDictionary<string, long> Tables)
{
    // The table that the object-relational mapper keeps the applied migrations in.
    private const string History = "__EFMigrationsHistory";

    /// <summary>The rows of all tables together.</summary>
    public long Rows => Tables.Values.Sum();

    /// <summary>
    /// Reads what a database holds, on a session of it. Inside a snapshot, every count is of
    /// the same moment.
    /// </summary>
    /// <remarks>
    /// The tables are listed from the catalog of the database, as their sizes are: no table of
    /// the application is named here, so a table that nobody thought of is counted too.
    /// </remarks>
    public static async Task<DatabaseContents> ReadAsync(NpgsqlConnection session, CancellationToken cancellationToken)
    {
        var names = new List<(string Table, string Quoted)>();

        await using (var list = new NpgsqlCommand(
            """
            SELECT tables.relname, quote_ident(schemas.nspname) || '.' || quote_ident(tables.relname)
            FROM pg_class AS tables
            JOIN pg_namespace AS schemas ON schemas.oid = tables.relnamespace
            WHERE schemas.nspname = current_schema() AND tables.relkind = 'r'
            ORDER BY tables.relname
            """,
            session))
        {
            await using var rows = await list.ExecuteReaderAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
            {
                names.Add((rows.GetString(0), rows.GetString(1)));
            }
        }

        var tables = new SortedDictionary<string, long>(StringComparer.Ordinal);
        string? migration = null;

        foreach (var (table, quoted) in names)
        {
            // The name of a table cannot be a parameter. It is no text of a request: the
            // database said it a moment ago, quoted as an identifier.
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {quoted}", session);
            tables[table] = (long)(await count.ExecuteScalarAsync(cancellationToken))!;

            if (table == History)
            {
                await using var latest = new NpgsqlCommand($"SELECT max(migration_id) FROM {quoted}", session);
                migration = await latest.ExecuteScalarAsync(cancellationToken) as string;
            }
        }

        return new DatabaseContents(migration, tables);
    }
}

/// <summary>
/// What a backup says of itself, in the file beside it: when and why it was taken, and what the
/// database held at that moment. A restore is held to it.
/// </summary>
internal sealed record BackupManifest(
    DateTimeOffset TakenAt,
    string Reason,
    string Database,
    string? Migration,
    IReadOnlyDictionary<string, long> Tables);

/// <summary>A backup that is stored: the file that <c>pg_dump</c> wrote, with its manifest.</summary>
/// <param name="Name">The name of the file, which is what the log and a recorded restore test call the backup.</param>
/// <param name="Path">Where the file is.</param>
/// <param name="Bytes">How large it is.</param>
/// <param name="Manifest">What it says of itself.</param>
internal sealed record DatabaseBackup(string Name, string Path, long Bytes, BackupManifest Manifest)
{
    /// <summary>What the database held when the backup was taken.</summary>
    public DatabaseContents Contents => new(Manifest.Migration, Manifest.Tables);
}

/// <summary>
/// Takes the backups of the database, and keeps them in the folder of <c>Backup:Directory</c>
/// (NFR-DAT-11). See docs/backups.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A backup is what <c>pg_dump</c> writes, in its custom format,</b> so that <c>pg_restore</c>
/// restores it, with or without the application.
/// </para>
/// <para>
/// <b>What the manifest counts is what the backup holds.</b> The rows are counted in a snapshot
/// of the database, and the tool is told to read that same snapshot: so a write that is made
/// while the backup is taken is in both, or in neither.
/// </para>
/// <para>
/// <b>A backup exists once its manifest does.</b> The tool writes to a file under another name,
/// which is given its name when the tool has ended well, and the manifest is written last. A
/// backup that was cut short leaves nothing that is taken for one.
/// </para>
/// <para>
/// <b>One backup is taken at a time,</b> and whoever asks for the latest one waits for the one
/// that is under way.
/// </para>
/// </remarks>
internal sealed class DatabaseBackups(
    BackupSettings settings,
    NpgsqlDataSource database,
    PostgresTools tools,
    IHostEnvironment host,
    TimeProvider clock,
    ILogger<DatabaseBackups> logger)
{
    private const string DumpExtension = ".dump";
    private const string ManifestExtension = ".json";
    private const string PartialExtension = ".partial";

    // A file that no backup is made of any more: one that a backup which was cut short left
    // behind. It is left alone for a day, in case another instance is still writing it.
    private static readonly TimeSpan LeftoverAfter = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions ManifestFormat = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Takes a backup of the database as it is now.</summary>
    /// <param name="reason">Why, as a word of <see cref="BackupReason"/>: it is part of the name of the backup.</param>
    /// <param name="cancellationToken">Stops the tool. Nothing is left of a backup that was stopped.</param>
    /// <exception cref="PostgresToolException">The tool could not be started, or ended with an error.</exception>
    public async Task<DatabaseBackup> TakeAsync(string reason, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var backup = await TakeOneAsync(reason, cancellationToken);

            logger.LogInformation(
                "The database was backed up to {Backup}: {Tables} tables, {Rows} rows, {Bytes} bytes",
                backup.Name,
                backup.Manifest.Tables.Count,
                backup.Contents.Rows,
                backup.Bytes);

            return backup;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The backup that was taken last, or null when there is none. It waits for one that is being taken.</summary>
    public async Task<DatabaseBackup?> LatestAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            return Stored().LastOrDefault();
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Deletes the backups that are kept no longer: those of longer ago than the days of
    /// <c>Backup:KeepDays</c>. The latest backup stays, however old it is. It says how many went.
    /// </summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();

            // A backup that is exactly as old as they are kept stays: it goes when it is older.
            var before = now.AddDays(-settings.KeepDays);
            var aged = Stored().SkipLast(1).Where(backup => backup.Manifest.TakenAt < before).ToList();

            foreach (var backup in aged)
            {
                // The manifest first: what is left when this is cut short is no backup.
                File.Delete(ManifestOf(backup.Path));
                File.Delete(backup.Path);
            }

            DeleteLeftovers(now);

            return aged.Count;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Opens the file of a backup, to restore it.</summary>
    public static Stream Open(DatabaseBackup backup) =>
        new FileStream(
            backup.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task<DatabaseBackup> TakeOneAsync(string reason, CancellationToken cancellationToken)
    {
        var folder = Folder();

        // One session, in one snapshot: what is counted here is what the tool reads, because
        // the tool is told the snapshot. The snapshot is there for as long as this transaction is.
        await using var session = await database.OpenConnectionAsync(cancellationToken);
        await using var snapshot = await session.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        string name;
        string exported;
        await using (var ask = new NpgsqlCommand("SELECT current_database(), pg_export_snapshot()", session))
        await using (var answer = await ask.ExecuteReaderAsync(cancellationToken))
        {
            await answer.ReadAsync(cancellationToken);
            name = answer.GetString(0);
            exported = answer.GetString(1);
        }

        var contents = await DatabaseContents.ReadAsync(session, cancellationToken);
        var takenAt = clock.GetUtcNow();

        var stem = FreeStem(
            folder,
            string.Create(CultureInfo.InvariantCulture, $"{FileNameOf(name)}-{takenAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{reason}"));
        var dump = Path.Combine(folder, stem + DumpExtension);
        var partial = dump + PartialExtension;

        try
        {
            await using (var file = Create(partial))
            {
                await tools.RunAsync(
                    settings.PgDump,
                    ["--format=custom", $"--snapshot={exported}", $"--dbname={name}"],
                    input: null,
                    output: file,
                    cancellationToken);

                await file.FlushAsync(cancellationToken);
            }

            File.Move(partial, dump);
        }
        catch
        {
            File.Delete(partial);

            throw;
        }

        var manifest = new BackupManifest(takenAt, reason, name, contents.Migration, contents.Tables);

        // Written last, and given its name when it is whole: with it the backup is there.
        var manifestPath = ManifestOf(dump);
        await using (var file = Create(manifestPath + PartialExtension))
        {
            await JsonSerializer.SerializeAsync(file, manifest, ManifestFormat, cancellationToken);
        }

        File.Move(manifestPath + PartialExtension, manifestPath);

        return new DatabaseBackup(Path.GetFileName(dump), dump, new FileInfo(dump).Length, manifest);
    }

    /// <summary>The backups that are stored, the oldest first.</summary>
    private List<DatabaseBackup> Stored()
    {
        var folder = Path.GetFullPath(settings.Directory, host.ContentRootPath);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var stored = new List<DatabaseBackup>();

        foreach (var manifestPath in Directory.EnumerateFiles(folder, "*" + ManifestExtension))
        {
            var dump = Path.ChangeExtension(manifestPath, DumpExtension);

            if (File.Exists(dump) && Read(manifestPath) is { } manifest)
            {
                stored.Add(new DatabaseBackup(Path.GetFileName(dump), dump, new FileInfo(dump).Length, manifest));
            }
        }

        return
        [
            .. stored
                .OrderBy(backup => backup.Manifest.TakenAt)
                .ThenBy(backup => backup.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>What a manifest says, or null for a file that is no manifest of a backup.</summary>
    private static BackupManifest? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);

            return JsonSerializer.Deserialize<BackupManifest>(file, ManifestFormat) is { Tables: not null, Reason: not null, Database: not null } manifest
                ? manifest
                : null;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // Another file of the folder, or a manifest that something else wrote over.
            return null;
        }
    }

    /// <summary>
    /// Deletes what a backup that was cut short left: a file that was being written, and a file
    /// of the tool that never got its manifest.
    /// </summary>
    private void DeleteLeftovers(DateTimeOffset now)
    {
        var folder = Path.GetFullPath(settings.Directory, host.ContentRootPath);
        if (!Directory.Exists(folder))
        {
            return;
        }

        var leftovers = Directory.EnumerateFiles(folder, "*" + PartialExtension)
            .Concat(Directory.EnumerateFiles(folder, "*" + DumpExtension).Where(dump => !File.Exists(ManifestOf(dump))));

        foreach (var leftover in leftovers.ToList())
        {
            if (now - File.GetLastWriteTimeUtc(leftover) > LeftoverAfter)
            {
                File.Delete(leftover);
            }
        }
    }

    /// <summary>The folder of the backups, which is made when it is not there, for the account of the application alone.</summary>
    private string Folder()
    {
        var folder = Path.GetFullPath(settings.Directory, host.ContentRootPath);

        if (!Directory.Exists(folder))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        return folder;
    }

    /// <summary>
    /// A new file that only the account of the application can read: a backup holds everything
    /// that the database holds.
    /// </summary>
    private static FileStream Create(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    /// <summary>A name that no backup of the folder has yet: two that are taken within a second differ by a number.</summary>
    private static string FreeStem(string folder, string stem)
    {
        var free = stem;

        for (var number = 2; Taken(free); number++)
        {
            free = string.Create(CultureInfo.InvariantCulture, $"{stem}-{number}");
        }

        return free;

        bool Taken(string candidate) =>
            File.Exists(Path.Combine(folder, candidate + DumpExtension))
            || File.Exists(Path.Combine(folder, candidate + ManifestExtension));
    }

    private static string ManifestOf(string dump) => Path.ChangeExtension(dump, ManifestExtension);

    /// <summary>The name of a database as part of the name of a file: what is no letter, digit, dot, dash, or underscore is an underscore.</summary>
    private static string FileNameOf(string database)
    {
        var name = new StringBuilder(database.Length);

        foreach (var character in database)
        {
            name.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }

        return name.ToString();
    }
}

/// <summary>
/// Backs the database up on its schedule, which is daily or more often, and deletes the backups
/// that are kept no longer (NFR-DAT-11). See <see cref="DatabaseBackups"/> and docs/backups.md.
/// </summary>
/// <remarks>
/// It is on an interval, and not at a time of the day: a backup that was missed while the
/// application was down is made up for at the next start, and a new database is backed up at once.
/// </remarks>
internal sealed class DatabaseBackupJob(DatabaseBackups backups, BackupSettings settings, ILogger<DatabaseBackupJob> logger)
    : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "database-backup";

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(TimeSpan.FromHours(settings.IntervalHours));

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await backups.TakeAsync(BackupReason.Scheduled, cancellationToken);

        var deleted = await backups.PruneAsync(cancellationToken);

        if (deleted > 0)
        {
            logger.LogInformation("{Backups} backups were deleted, which were kept no longer", deleted);
        }
    }
}
