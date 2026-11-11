using System.Text.Json;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// If a migration fails, the application logs the error and does not start (NFR-DAT-3). The
/// error is logged once, in full, and without the database password (NFR-LOG-4).
/// </summary>
public class MigrationFailureTests
{
    private const string Marker = "SECRET-MARKER";

    [Fact]
    public void A_server_that_cannot_be_reached_is_logged_and_the_application_does_not_start()
    {
        // Nothing listens on port 1, so the connection is refused at once.
        var (exitCode, events, log) = Run($"Host=127.0.0.1;Port=1;Database=ecnanib;Username=app;Password={Marker}");

        Assert.Equal(1, exitCode);
        AssertLoggedOnce(events, cause: "Failed to connect to 127.0.0.1:1");
        Assert.DoesNotContain(Marker, log);
    }

    [Fact]
    public void A_database_that_does_not_exist_is_logged_and_is_not_created()
    {
        var name = $"missing_{Guid.NewGuid():N}";

        var (exitCode, events, log) = Run(PostgresServer.ConnectionStringTo(name));

        Assert.Equal(1, exitCode);
        AssertLoggedOnce(events, cause: "3D000");
        Assert.DoesNotContain("database-password", log);

        // The database is the deployment's to create. The application needs no right to.
        Assert.Equal(0L, PostgresServer.SharedDatabase.Scalar<long>($"SELECT count(*) FROM pg_database WHERE datname = '{name}'"));
    }

    [Fact]
    public void A_migration_that_fails_is_logged_and_leaves_nothing_half_done()
    {
        // The first migration creates the table of roles, and here something else already did.
        var database = PostgresServer.CreateDatabase();
        database.Execute("CREATE TABLE roles (unrelated integer)");

        var (exitCode, events, log) = Run(database.ConnectionString);

        Assert.Equal(1, exitCode);
        AssertLoggedOnce(events, cause: "42P07");
        Assert.DoesNotContain("database-password", log);

        // The tables the migration created before it failed are gone again, and it is not recorded.
        Assert.Equal(["__EFMigrationsHistory", "roles"], database.Tables());
        Assert.Empty(database.AppliedMigrations());
        Assert.Equal(
            ["unrelated"],
            database.Column("SELECT column_name::text FROM information_schema.columns WHERE table_name = 'roles'"));
    }

    [Fact]
    public void A_model_that_is_ahead_of_its_migrations_stops_the_application()
    {
        // The model gets one more entity than the migrations know of, as when an entity was
        // added and no migration generated.
        var database = PostgresServer.CreateDatabase();
        using var contentRoot = new SettingsDirectory(SettingsJson.Valid().OnDatabase(database));
        using var application = new EcnanibApplicationFactory(
            contentRoot,
            host => host.ConfigureServices(services => services.ConfigureDbContext<EcnanibDbContext>(
                options => options.ReplaceService<IModelCustomizer, OneEntityMore>())));

        var refused = Assert.ThrowsAny<Exception>(() => application.Services.GetRequiredService<IServiceProvider>());

        Assert.StartsWith("The application did not start.", refused.Message);
        Assert.Contains("The database could not be migrated, so Ecnanib does not start.", refused.Message);
        Assert.Contains("has pending changes", refused.Message);
        Assert.Empty(database.AppliedMigrations());
    }

    [Fact]
    public void The_test_host_does_not_start_either_and_says_why()
    {
        using var contentRoot = new SettingsDirectory(SettingsJson.Valid()
            .With("ConnectionStrings:EcnanibDbContext", $"Host=127.0.0.1;Port=1;Database=ecnanib;Username=app;Password={Marker}"));
        using var application = new EcnanibApplicationFactory(contentRoot);

        var refused = Assert.ThrowsAny<Exception>(() => application.Services.GetRequiredService<IServiceProvider>());

        // What the application logged, so that a test which cannot start says what is wrong.
        Assert.StartsWith("The application did not start.", refused.Message);
        Assert.Contains("The database could not be migrated, so Ecnanib does not start.", refused.Message);
        Assert.DoesNotContain(Marker, refused.Message);
    }

    /// <summary>The application's model, and one entity that no migration creates a table for.</summary>
    private sealed class OneEntityMore(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);

            modelBuilder.Entity<Unmigrated>().ToTable("unmigrated");
        }
    }

    private sealed class Unmigrated
    {
        public int Id { get; set; }
    }

    /// <summary>One event, at the highest level, with the error in full and what caused it.</summary>
    private static void AssertLoggedOnce(IReadOnlyList<LoggedEvent> events, string cause)
    {
        var error = Assert.Single(events, written => written.Level is "Error" or "Fatal");

        Assert.Equal("Fatal", error.Level);
        Assert.Contains("Ecnanib.Platform.Persistence.DatabaseMigrationException", error.Exception);
        Assert.Contains("The database could not be migrated, so Ecnanib does not start.", error.Exception);
        Assert.Contains(cause, error.Exception);
        Assert.Contains("   at ", error.Exception);
    }

    /// <summary>
    /// Runs the application's entry point on a database it cannot migrate, and returns its exit
    /// code and what it wrote to its log file. It returns at once, because it does not start.
    /// </summary>
    private static (int ExitCode, IReadOnlyList<LoggedEvent> Events, string Log) Run(string connectionString)
    {
        using var contentRoot = new SettingsDirectory(SettingsJson.Valid()
            .With("ConnectionStrings:EcnanibDbContext", connectionString)
            .With("Logging:MinimumLevel", "Information")
            .Without("Logging:Outputs:Console"));
        using var errors = new StringWriter();

        // The entry point runs inside the test process here, so the host is told which
        // application it is. It would take the name of the test assembly, and look for the
        // application's static files under that name.
        string[] arguments = [$"--contentRoot={contentRoot.Path}", $"--applicationName={typeof(Program).Assembly.GetName().Name}"];

        // On a thread of its own, with a limit: an application that starts after all would
        // otherwise run for as long as the tests do.
        var run = Task.Run(() => Program.Run(arguments, errors));
        Assert.True(
            run.Wait(TimeSpan.FromSeconds(60)),
            "The application did not return. It started, or it is still trying to.");

        // Nothing about the configuration file: that is the other way of refusing to start.
        Assert.Empty(errors.ToString());

        var folder = Path.Combine(contentRoot.Path, "logs");
        var lines = Directory.Exists(folder)
            ? Directory.GetFiles(folder).Order(StringComparer.Ordinal).SelectMany(File.ReadAllLines).Where(line => line.Length > 0).ToList()
            : [];

        return (
            run.Result,
            [.. lines.Select(line => new LoggedEvent(JsonDocument.Parse(line).RootElement.Clone()))],
            string.Join(Environment.NewLine, lines));
    }
}
