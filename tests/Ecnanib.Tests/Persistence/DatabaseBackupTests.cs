using System.Globalization;
using System.Text;
using Ecnanib.Tests.Architecture;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// The database is backed up daily (NFR-DAT-11). A job takes a backup with the client tool of
/// PostgreSQL, keeps it in a folder with a manifest of what it holds, and deletes the backups
/// that are kept no longer. See docs/backups.md.
/// </summary>
public class DatabaseBackupTests
{
    private const string BackedUp = "The database was backed up to {Backup}: {Tables} tables, {Rows} rows, {Bytes} bytes";
    private const string Deleted = "{Backups} backups were deleted, which were kept no longer";

    [Fact]
    public void The_job_backs_the_database_up_every_hours_of_the_configuration_file()
    {
        using var application = new BackupApplication();

        Assert.Equal("database-backup", application.BackupJob.Name);

        // On an interval: a backup that was missed while the application was down is made up
        // for at the next start, and a database that was never backed up is backed up at once.
        Assert.Equal(TimeSpan.FromHours(12), application.BackupJob.Schedule.Period);
        Assert.False(application.BackupJob.Schedule.OnUtcBoundaries);
    }

    [Fact]
    public async Task A_run_leaves_a_backup_of_the_tool_and_a_manifest_of_what_it_holds()
    {
        using var application = new BackupApplication();

        await application.BackUpAsync();

        // The backup and its manifest, and nothing that was being written.
        var name = $"{application.Database.Name}-20261127T031500Z-scheduled";
        Assert.Equal([$"{name}.dump", $"{name}.json"], application.Files());

        // What pg_dump writes in its custom format, which pg_restore reads.
        var dump = File.ReadAllBytes(application.PathOf($"{name}.dump"));
        Assert.Equal("PGDMP", Encoding.ASCII.GetString(dump, 0, 5));

        var manifest = application.ManifestOf($"{name}.dump");

        Assert.Equal(BackupApplication.Start, manifest.TakenAt);
        Assert.Equal("scheduled", manifest.Reason);
        Assert.Equal(application.Database.Name, manifest.Database);
        Assert.Equal(application.Database.AppliedMigrations()[^1], manifest.Migration);

        // Every table of the database, the one that keeps the migrations among them, with its rows.
        Assert.Equal(application.Database.Tables(), manifest.Tables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(application.Database.AppliedMigrations().Count, manifest.Tables["__EFMigrationsHistory"]);
        Assert.Equal(application.Count("roles"), manifest.Tables["roles"]);
        Assert.Equal(application.Count("prompt_templates"), manifest.Tables["prompt_templates"]);
        Assert.Equal(0, manifest.Tables["users"]);
        Assert.True(manifest.Tables["roles"] > 0);

        var logged = Assert.Single(application.Events(BackupApplication.BackupsArea));
        Assert.Equal(BackedUp, logged.Template);
        Assert.Equal("Information", logged.Level);
        Assert.Equal($"{name}.dump", logged.Text("Backup"));
        Assert.Equal(manifest.Tables.Count, logged["Tables"].GetInt32());
        Assert.Equal(manifest.Rows, logged["Rows"].GetInt64());
        Assert.Equal(dump.LongLength, logged["Bytes"].GetInt64());
    }

    [Fact]
    public async Task A_backup_is_for_the_account_of_the_application_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no permission bits to check; see docs/backups.md.");

        using var application = new BackupApplication();

        await application.BackUpAsync();

        // A backup holds everything that the database holds.
#pragma warning disable CA1416 // Skipped on Windows above.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(application.Folder));
        Assert.All(
            application.Files(),
            file => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(application.PathOf(file))));
#pragma warning restore CA1416
    }

    [Fact]
    public async Task Two_backups_of_one_second_are_two_backups()
    {
        using var application = new BackupApplication();

        await application.BackUpAsync();
        await application.BackUpAsync();

        var name = $"{application.Database.Name}-20261127T031500Z-scheduled";
        Assert.Equal([$"{name}-2.dump", $"{name}.dump"], application.Backups());
    }

    [Fact]
    public async Task A_tool_that_is_not_there_fails_the_run_and_leaves_no_backup()
    {
        using var application = new BackupApplication(
            settings: file => file.With("Backup:PgDump", "/nowhere/pg_dump"));

        var failed = await Assert.ThrowsAnyAsync<Exception>(() => application.BackUpAsync());

        Assert.Equal("Ecnanib.Platform.Persistence.PostgresToolException", failed.GetType().FullName);
        Assert.StartsWith("/nowhere/pg_dump could not be started.", failed.Message);
        Assert.Empty(application.Files());
        Assert.Empty(application.Events(BackupApplication.BackupsArea));
    }

    [Fact]
    public async Task A_tool_that_fails_fails_the_run_with_what_the_tool_said_and_leaves_no_backup()
    {
        using var tools = new SettingsDirectory();
        var failing = ServerTools.Script(tools.Path, "pg_dump", "echo 'half a backup'; echo 'pg_dump: error: no room' >&2; exit 3");
        using var application = new BackupApplication(settings: file => file.With("Backup:PgDump", failing));

        var failed = await Assert.ThrowsAnyAsync<Exception>(() => application.BackUpAsync());

        Assert.Equal("Ecnanib.Platform.Persistence.PostgresToolException", failed.GetType().FullName);
        Assert.Equal($"{failing} ended with code 3: pg_dump: error: no room", failed.Message);

        // Not what the tool had written by then, either.
        Assert.Empty(application.Files());
    }

    [Fact]
    public async Task The_tool_is_told_the_connection_in_its_own_variables_and_no_secret_as_an_argument()
    {
        using var tools = new SettingsDirectory();
        var said = Path.Combine(tools.Path, "said");

        // What it was started with, and then nothing that is a backup.
        var telling = ServerTools.Script(
            tools.Path,
            "pg_dump",
            $"""echo "$@" > "{said}"; echo "$PGHOST $PGPORT $PGUSER $PGPASSWORD $PGSSLMODE" >> "{said}"; exit 1""");
        using var application = new BackupApplication(settings: file => file.With("Backup:PgDump", telling));

        await Assert.ThrowsAnyAsync<Exception>(() => application.BackUpAsync());

        var lines = File.ReadAllLines(said);
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(application.Database.ConnectionString);

        // The format that pg_restore reads, the snapshot that the rows were counted in, and the database.
        var arguments = lines[0].Split(' ');
        Assert.Equal(3, arguments.Length);
        Assert.Equal("--format=custom", arguments[0]);
        Assert.Matches("^--snapshot=[0-9A-F]+-[0-9A-F]+-[0-9]+$", arguments[1]);
        Assert.Equal($"--dbname={application.Database.Name}", arguments[2]);

        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{connection.Host} {connection.Port} {connection.Username} {connection.Password} prefer"),
            lines[1]);
        Assert.DoesNotContain(connection.Password!, lines[0]);
    }

    [Fact]
    public async Task A_run_that_is_stopped_stops_the_tool_and_leaves_no_backup()
    {
        using var tools = new SettingsDirectory();
        var started = Path.Combine(tools.Path, "started");
        var waiting = ServerTools.Script(tools.Path, "pg_dump", $"""echo 'half a backup'; touch "{started}"; exec sleep 60""");
        using var application = new BackupApplication(settings: file => file.With("Backup:PgDump", waiting));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = application.BackUpAsync(stop.Token);
        await Wait.UntilAsync(() => File.Exists(started), "the tool being started");

        await stop.CancelAsync();

        // At once, and not when the tool would have ended by itself.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.Empty(application.Files());
    }

    [Fact]
    public async Task A_backup_that_is_kept_no_longer_is_deleted_and_one_that_is_exactly_as_old_stays()
    {
        using var application = new BackupApplication();
        var prefix = application.Database.Name;

        // The file of the tests keeps a backup for 10 days.
        await application.BackUpAsync();

        application.Clock.Set(BackupApplication.Start.AddDays(5));
        await application.BackUpAsync();

        application.Clock.Set(BackupApplication.Start.AddDays(10));
        await application.BackUpAsync();

        Assert.Equal(
            [$"{prefix}-20261127T031500Z-scheduled.dump", $"{prefix}-20261202T031500Z-scheduled.dump", $"{prefix}-20261207T031500Z-scheduled.dump"],
            application.Backups());
        Assert.DoesNotContain(application.Events("Ecnanib.Platform.Persistence.DatabaseBackupJob"), logged => logged.Template == Deleted);

        // A second later the first is older than it is kept, and goes with its manifest.
        application.Clock.Set(BackupApplication.Start.AddDays(10).AddSeconds(1));
        await application.BackUpAsync();

        Assert.Equal(
            [
                $"{prefix}-20261202T031500Z-scheduled.dump", $"{prefix}-20261202T031500Z-scheduled.json",
                $"{prefix}-20261207T031500Z-scheduled.dump", $"{prefix}-20261207T031500Z-scheduled.json",
                $"{prefix}-20261207T031501Z-scheduled.dump", $"{prefix}-20261207T031501Z-scheduled.json",
            ],
            application.Files());

        var deleted = Assert.Single(application.Events("Ecnanib.Platform.Persistence.DatabaseBackupJob"));
        Assert.Equal(Deleted, deleted.Template);
        Assert.Equal(1, deleted["Backups"].GetInt32());

        // After a long time away, the backup of that run is the one that is left.
        application.Clock.Set(BackupApplication.Start.AddDays(400));
        await application.BackUpAsync();

        Assert.Equal([$"{prefix}-20280101T031500Z-scheduled.dump"], application.Backups());
    }

    [Fact]
    public async Task What_a_backup_that_was_cut_short_left_is_deleted_after_a_day_and_is_taken_for_no_backup()
    {
        using var application = new BackupApplication();
        await application.BackUpAsync();

        var backup = Assert.Single(application.Backups());

        // A file that was being written, and a file of the tool that never got its manifest:
        // one of each from two days ago, and one of each from now.
        foreach (var (file, age) in new[]
        {
            ("old-scheduled.dump.partial", TimeSpan.FromDays(2)),
            ("old-scheduled.dump", TimeSpan.FromDays(2)),
            ("new-scheduled.dump.partial", TimeSpan.Zero),
            ("new-scheduled.dump", TimeSpan.Zero),
        })
        {
            File.WriteAllText(application.PathOf(file), "half a backup");
            File.SetLastWriteTimeUtc(application.PathOf(file), (BackupApplication.Start - age).UtcDateTime);
        }

        await application.BackUpAsync();

        Assert.Equal(
            [
                "new-scheduled.dump", "new-scheduled.dump.partial",
                Path.ChangeExtension(backup, null) + "-2.dump", Path.ChangeExtension(backup, null) + "-2.json",
                backup, Path.ChangeExtension(backup, ".json"),
            ],
            application.Files());
    }

    [Fact]
    public void Only_what_runs_the_tools_of_PostgreSQL_starts_a_process()
    {
        // Pinned, so that another shows in a review as what it is: one more thing that the
        // application starts on the machine that it runs on.
        Assert.Equal(
            ["Ecnanib.Platform.Persistence.PostgresTools"],
            DependencyChecker.FindCallersOf(typeof(Program).Assembly, "System.Diagnostics.Process::Start")
                .Select(caller => caller.Split('+')[0])
                .Distinct());
    }
}
