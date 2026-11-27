using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// A migration that is marked as destructive is preceded by a backup (NFR-DAT-11): the
/// application backs the database up as it starts, before it applies the pending migrations,
/// and applies none of them when the backup fails. See docs/backups.md.
/// </summary>
public class MigrationBackupTests
{
    private const string Migrator = "Ecnanib.Platform.Persistence.DatabaseMigrator";
    private const string Applying = "Applying {MigrationCount} database migrations, from {FirstMigration} to {LastMigration}";
    private const string BackingUp = "Backing up the database before the migration {Migration}, which is marked as destructive";
    private const string BackedUp = "The database was backed up to {Backup}: {Tables} tables, {Rows} rows, {Bytes} bytes";
    private const string Migrated = "The database was migrated to {Migration}";

    // The migration that the database of a test is at, and the first one after it that is marked.
    private const string Accounts = "20261111172937_AddAccounts";
    private const string SignIn = "20261111183920_AddSignIn";

    [Fact]
    public void A_database_is_backed_up_before_a_destructive_migration_is_applied_to_it()
    {
        var database = DatabaseAt(Accounts);
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        _ = application.Services;

        // The backup says which migration it is before, and holds the database as it was then.
        var backups = Path.Combine(application.ContentRoot.Path, "backups");
        var backup = Assert.Single(Directory.GetFiles(backups, "*.dump"));
        Assert.EndsWith($"-before-{SignIn}.dump", backup);

        var manifest = StoredManifest.Read(Path.ChangeExtension(backup, ".json"));
        Assert.Equal($"before-{SignIn}", manifest.Reason);
        Assert.Equal(Accounts, manifest.Migration);
        Assert.Contains("users", manifest.Tables.Keys);
        Assert.DoesNotContain("sessions", manifest.Tables.Keys);

        // And then the migrations were applied.
        Assert.Equal(AllMigrations(), database.AppliedMigrations());

        var told = application.Events()
            .Where(logged => logged.Template is Applying or BackingUp or BackedUp or Migrated)
            .Select(logged => logged.Template);
        Assert.Equal([Applying, BackingUp, BackedUp, Migrated], told);

        var backingUp = Assert.Single(application.Events(), logged => logged.Template == BackingUp);
        Assert.Equal(Migrator, backingUp.Area);
        Assert.Equal(SignIn, backingUp.Text("Migration"));
    }

    [Fact]
    public void A_database_that_nothing_was_applied_to_is_not_backed_up()
    {
        // Every migration is pending, the marked ones among them, and there is nothing to lose.
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        _ = application.Services;

        Assert.Equal(AllMigrations(), database.AppliedMigrations());
        Assert.False(Directory.Exists(Path.Combine(application.ContentRoot.Path, "backups")));
        Assert.DoesNotContain(application.Events(), logged => logged.Template is BackingUp or BackedUp);
    }

    [Fact]
    public void Migrations_of_which_none_is_marked_are_applied_without_a_backup()
    {
        // From the last migration that is marked on, the pending ones only add.
        var database = DatabaseAt(LastMarked());
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        _ = application.Services;

        Assert.Equal(AllMigrations(), database.AppliedMigrations());
        Assert.Single(application.Events(), logged => logged.Template == Migrated);
        Assert.False(Directory.Exists(Path.Combine(application.ContentRoot.Path, "backups")));
        Assert.DoesNotContain(application.Events(), logged => logged.Template is BackingUp or BackedUp);
    }

    [Fact]
    public void A_database_that_is_up_to_date_is_not_backed_up()
    {
        var database = PostgresServer.CreateDatabase();
        database.Migrate();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        _ = application.Services;

        Assert.False(Directory.Exists(Path.Combine(application.ContentRoot.Path, "backups")));
    }

    [Fact]
    public void When_the_backup_fails_no_migration_is_applied_and_the_application_does_not_start()
    {
        var database = DatabaseAt(Accounts);
        using var contentRoot = new SettingsDirectory(SettingsJson.Valid()
            .With("Backup:PgDump", "/nowhere/pg_dump")
            .OnDatabase(database));
        using var application = new EcnanibApplicationFactory(contentRoot);

        var refused = Assert.ThrowsAny<Exception>(() => application.Services.GetRequiredService<IServiceProvider>());

        Assert.StartsWith("The application did not start.", refused.Message);
        Assert.Contains("The database could not be migrated, so Ecnanib does not start.", refused.Message);
        Assert.Contains("/nowhere/pg_dump could not be started.", refused.Message);

        // The database is where it was, with what it held.
        Assert.Equal(Accounts, database.AppliedMigrations()[^1]);
        Assert.DoesNotContain("sessions", database.Tables());
    }

    /// <summary>A database that an earlier version of the application left: migrated up to a migration, and no further.</summary>
    private static TestDatabase DatabaseAt(string migration)
    {
        var database = PostgresServer.CreateDatabase();

        using var context = database.Context();
        context.GetService<IMigrator>().Migrate(migration);

        return database;
    }

    /// <summary>The migrations in the application, in order.</summary>
    private static IReadOnlyList<string> AllMigrations()
    {
        using var context = ModelOnly.Context();

        return [.. context.Database.GetMigrations()];
    }

    /// <summary>The latest migration that is marked as destructive.</summary>
    private static string LastMarked()
    {
        using var context = ModelOnly.Context();

        return context.GetService<IMigrationsAssembly>().Migrations
            .Last(migration => migration.Value.IsDefined(typeof(DestructiveMigrationAttribute), inherit: false))
            .Key;
    }
}
