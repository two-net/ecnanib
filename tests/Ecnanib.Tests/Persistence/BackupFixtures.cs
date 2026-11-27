using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// The application on a database of the test's own, with a clock that the test moves, and with
/// the job that backs the database up and the job that tests a restore for the test to run: the
/// scheduler of no application of the tests runs either. See docs/backups.md.
/// </summary>
/// <remarks>
/// The tools are those of the test server, which run inside its container, unless the test names
/// tools of its own: see <see cref="ServerTools"/>.
/// </remarks>
internal sealed class BackupApplication : IDisposable
{
    /// <summary>The time that the clock of the application starts at.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 27, 3, 15, 0, TimeSpan.Zero);

    /// <summary>The area that the backups log under.</summary>
    public const string BackupsArea = "Ecnanib.Platform.Persistence.DatabaseBackups";

    private readonly LoggedApplication _application;

    /// <param name="database">The database, when the test made it before the application starts on it.</param>
    /// <param name="settings">What the test changes in the configuration file, such as a tool.</param>
    public BackupApplication(TestDatabase? database = null, Func<SettingsJson, SettingsJson>? settings = null)
    {
        Database = database ?? PostgresServer.CreateDatabase();

        var file = LoggedApplication.Settings();

        _application = new LoggedApplication(
            settings?.Invoke(file) ?? file,
            host => host.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock)),
            Database);

        // Started here, so that the database is migrated before the test asks it anything.
        _ = _application.Services;
    }

    public TestDatabase Database { get; }

    public TestClock Clock { get; } = new(Start);

    /// <summary>The folder of the backups: <c>backups</c> under the folder that the application runs from.</summary>
    public string Folder => Path.Combine(_application.ContentRoot.Path, "backups");

    /// <summary>A folder beside the backups, for a tool of the test's own.</summary>
    public string ContentRoot => _application.ContentRoot.Path;

    /// <summary>The name of the database that a restore is tested in.</summary>
    public string Scratch => Database.Name + "_restore_test";

    public IBackgroundJob BackupJob =>
        _application.Services.GetRequiredKeyedService<IBackgroundJob>(EcnanibApplicationFactory.DatabaseBackupKey);

    public IBackgroundJob RestoreTestJob =>
        _application.Services.GetRequiredKeyedService<IBackgroundJob>(EcnanibApplicationFactory.RestoreTestKey);

    /// <summary>Runs the job that backs the database up, now, as the scheduler does on its schedule.</summary>
    public Task BackUpAsync(CancellationToken? cancellationToken = null) =>
        BackupJob.RunAsync(new BackgroundJobRun(Clock.GetUtcNow()), cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>Runs the job that tests a restore, now, as the scheduler does on its schedule.</summary>
    public Task TestRestoreAsync(CancellationToken? cancellationToken = null) =>
        RestoreTestJob.RunAsync(new BackgroundJobRun(Clock.GetUtcNow()), cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>The names of the files in the folder of the backups, in order. None when the folder is not there.</summary>
    public IReadOnlyList<string> Files() =>
        Directory.Exists(Folder)
            ? [.. Directory.GetFiles(Folder).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>The names of the backups in the folder: the files that the tool wrote, in order.</summary>
    public IReadOnlyList<string> Backups() => [.. Files().Where(file => file.EndsWith(".dump", StringComparison.Ordinal))];

    public string PathOf(string file) => Path.Combine(Folder, file);

    /// <summary>What the manifest of a backup says, by the name of the backup.</summary>
    public StoredManifest ManifestOf(string backup) => StoredManifest.Read(ManifestPathOf(backup));

    public string ManifestPathOf(string backup) => PathOf(Path.ChangeExtension(backup, ".json"));

    /// <summary>The tests of a restore that are recorded, in the order they started in.</summary>
    public IReadOnlyList<StoredRestoreTest> RestoreTests()
    {
        using var connection = Database.Open();
        using var command = new NpgsqlCommand(
            "SELECT backup, backup_taken_at, started_at, finished_at, result, failure, tables, rows FROM restore_tests ORDER BY started_at, id",
            connection);
        using var reader = command.ExecuteReader();

        var tests = new List<StoredRestoreTest>();
        while (reader.Read())
        {
            tests.Add(new StoredRestoreTest(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt64(7)));
        }

        return tests;
    }

    /// <summary>Whether the server has the database that a restore is tested in.</summary>
    public bool HasScratch() =>
        PostgresServer.SharedDatabase.Scalar<long>($"SELECT count(*) FROM pg_database WHERE datname = '{Scratch}'") == 1;

    /// <summary>How many rows a table of the application's database holds.</summary>
    public long Count(string table) => Database.Scalar<long>($"""SELECT count(*) FROM "{table}" """);

    /// <summary>The events that an area logged, in order.</summary>
    public IReadOnlyList<LoggedEvent> Events(string area) => [.. _application.Events().Where(logged => logged.Area == area)];

    public void Dispose() => _application.Dispose();
}

/// <summary>What the manifest of a backup says, as its file has it.</summary>
internal sealed record StoredManifest(
    DateTimeOffset TakenAt,
    string Reason,
    string Database,
    string? Migration,
    IReadOnlyDictionary<string, long> Tables)
{
    public long Rows => Tables.Values.Sum();

    public static StoredManifest Read(string path)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        return new StoredManifest(
            json["takenAt"]!.GetValue<DateTimeOffset>(),
            json["reason"]!.GetValue<string>(),
            json["database"]!.GetValue<string>(),
            json["migration"]?.GetValue<string>(),
            json["tables"]!.AsObject().ToDictionary(table => table.Key, table => table.Value!.GetValue<long>()));
    }

    /// <summary>Writes another number of rows for a table into a manifest, as if the backup had said so.</summary>
    public static void Rewrite(string path, string table, long rows)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["tables"]![table] = rows;

        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>A row of the <c>restore_tests</c> table, read by a test directly.</summary>
internal sealed record StoredRestoreTest(
    string Backup,
    DateTimeOffset BackupTakenAt,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Result,
    string? Failure,
    int Tables,
    long Rows);
