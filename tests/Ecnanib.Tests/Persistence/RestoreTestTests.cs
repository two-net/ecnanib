using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Restores are tested, and the result of each test is recorded (NFR-DAT-11). A job restores the
/// latest backup to a database of its own, holds that database to what the backup says of
/// itself, and writes how the test ended to <c>restore_tests</c>. See docs/backups.md.
/// </summary>
public class RestoreTestTests
{
    private const string Area = "Ecnanib.Platform.Persistence.RestoreTests";

    private const string Restored =
        "The backup {Backup} was restored to a database of its own, which holds what the backup says: {Tables} tables, {Rows} rows";

    [Fact]
    public void The_job_tests_a_restore_every_days_of_the_configuration_file()
    {
        using var application = new BackupApplication();

        Assert.Equal("restore-test", application.RestoreTestJob.Name);
        Assert.Equal(TimeSpan.FromDays(3), application.RestoreTestJob.Schedule.Period);
        Assert.False(application.RestoreTestJob.Schedule.OnUtcBoundaries);
    }

    [Fact]
    public async Task The_latest_backup_is_restored_to_a_database_of_its_own_and_the_test_is_recorded()
    {
        using var application = new BackupApplication();

        await application.BackUpAsync();
        application.Clock.Set(BackupApplication.Start.AddHours(1));
        await application.BackUpAsync();

        var latest = application.Backups()[^1];
        var manifest = application.ManifestOf(latest);
        var tables = application.Database.Tables();

        application.Clock.Set(BackupApplication.Start.AddHours(2));
        await application.TestRestoreAsync();

        var test = Assert.Single(application.RestoreTests());

        Assert.Equal(
            new StoredRestoreTest(
                Backup: latest,
                BackupTakenAt: BackupApplication.Start.AddHours(1),
                StartedAt: BackupApplication.Start.AddHours(2),
                FinishedAt: BackupApplication.Start.AddHours(2),
                Result: "Succeeded",
                Failure: null,
                Tables: manifest.Tables.Count,
                Rows: manifest.Rows),
            test);
        Assert.True(test.Rows > 0);

        // The database that it was restored to is gone, and the one of the application is as it was.
        Assert.False(application.HasScratch());
        Assert.Equal(tables, application.Database.Tables());
        Assert.Equal(2, application.Backups().Count);

        var logged = Assert.Single(application.Events(Area));
        Assert.Equal(Restored, logged.Template);
        Assert.Equal("Information", logged.Level);
        Assert.Equal(latest, logged.Text("Backup"));
        Assert.Equal(test.Tables, logged["Tables"].GetInt32());
        Assert.Equal(test.Rows, logged["Rows"].GetInt64());
    }

    [Fact]
    public async Task What_was_written_since_the_backup_does_not_fail_the_test()
    {
        using var application = new BackupApplication();

        await application.BackUpAsync();
        var manifest = application.ManifestOf(Assert.Single(application.Backups()));

        // The database goes on, and the backup is held to what it holds itself.
        application.Database.Execute(
            "INSERT INTO background_jobs (name, last_started_at) SELECT 'planted-' || number, now() FROM generate_series(1, 25) AS number");

        await application.TestRestoreAsync();

        var test = Assert.Single(application.RestoreTests());
        Assert.Equal("Succeeded", test.Result);
        Assert.Equal(manifest.Rows, test.Rows);
    }

    [Fact]
    public async Task With_no_backup_yet_the_test_takes_one_and_restores_it()
    {
        using var application = new BackupApplication();

        await application.TestRestoreAsync();

        var backup = Assert.Single(application.Backups());
        Assert.Equal($"{application.Database.Name}-20261127T031500Z-restore-test.dump", backup);

        var test = Assert.Single(application.RestoreTests());
        Assert.Equal(backup, test.Backup);
        Assert.Equal("Succeeded", test.Result);
    }

    [Fact]
    public async Task A_backup_that_is_not_restored_is_recorded_and_fails_the_run()
    {
        using var application = new BackupApplication();

        await application.BackUpAsync();
        var backup = Assert.Single(application.Backups());

        // Half of what the tool wrote.
        using (var file = new FileStream(application.PathOf(backup), FileMode.Open, FileAccess.ReadWrite))
        {
            file.SetLength(file.Length / 2);
        }

        var failed = await Assert.ThrowsAnyAsync<Exception>(() => application.TestRestoreAsync());

        Assert.Equal("Ecnanib.Platform.Persistence.RestoreTestFailedException", failed.GetType().FullName);
        Assert.Equal($"The restore of the backup {backup} was tested, and failed.", failed.Message);

        // Why is in the error, which the scheduler logs, and not in the row.
        Assert.Equal("Ecnanib.Platform.Persistence.PostgresToolException", failed.InnerException?.GetType().FullName);
        Assert.Contains("pg_restore ended with code 1:", failed.InnerException!.Message);

        var test = Assert.Single(application.RestoreTests());
        Assert.Equal(backup, test.Backup);
        Assert.Equal("Failed", test.Result);
        Assert.Equal("NotRestored", test.Failure);
        Assert.Equal(0, test.Tables);
        Assert.Equal(0, test.Rows);

        Assert.False(application.HasScratch());
        Assert.Empty(application.Events(Area));
    }

    [Fact]
    public async Task A_restored_database_that_is_not_what_the_backup_says_is_recorded_and_fails_the_run()
    {
        using var application = new BackupApplication();

        await application.BackUpAsync();
        var backup = Assert.Single(application.Backups());
        var manifest = application.ManifestOf(backup);

        // The backup says one role more than it holds, as it would of a row that the restore lost.
        StoredManifest.Rewrite(application.ManifestPathOf(backup), "roles", manifest.Tables["roles"] + 1);

        var failed = await Assert.ThrowsAnyAsync<Exception>(() => application.TestRestoreAsync());

        Assert.Equal($"The restore of the backup {backup} was tested, and failed.", failed.Message);
        Assert.Equal(
            "The database that came of the backup is not what the backup says: " +
            $"the table roles holds {manifest.Tables["roles"]} rows in place of {manifest.Tables["roles"] + 1}.",
            failed.InnerException?.Message);

        var test = Assert.Single(application.RestoreTests());
        Assert.Equal("Failed", test.Result);
        Assert.Equal("Differs", test.Failure);

        // What the restored database held.
        Assert.Equal(manifest.Tables.Count, test.Tables);
        Assert.Equal(manifest.Rows, test.Rows);

        Assert.False(application.HasScratch());
    }

    [Fact]
    public async Task A_database_that_an_earlier_test_left_is_dropped_first()
    {
        using var application = new BackupApplication();

        // What a test leaves when its process ends before it is done.
        PostgresServer.SharedDatabase.Execute($"""CREATE DATABASE "{application.Scratch}" """);
        new TestDatabase(PostgresServer.ConnectionStringTo(application.Scratch)).Execute("CREATE TABLE roles (leftover integer)");

        await application.BackUpAsync();
        await application.TestRestoreAsync();

        Assert.Equal("Succeeded", Assert.Single(application.RestoreTests()).Result);
        Assert.False(application.HasScratch());
    }

    [Fact]
    public async Task A_test_that_is_stopped_records_nothing_and_leaves_no_database()
    {
        using var tools = new SettingsDirectory();
        var started = Path.Combine(tools.Path, "started");
        var waiting = ServerTools.Script(tools.Path, "pg_restore", $"""touch "{started}"; exec sleep 60""");
        using var application = new BackupApplication(settings: file => file.With("Backup:PgRestore", waiting));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await application.BackUpAsync();

        var run = application.TestRestoreAsync(stop.Token);
        await Wait.UntilAsync(() => File.Exists(started), "the tool being started");
        Assert.True(application.HasScratch());

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        // A test that was stopped is no test.
        Assert.Empty(application.RestoreTests());
        Assert.False(application.HasScratch());
    }
}
