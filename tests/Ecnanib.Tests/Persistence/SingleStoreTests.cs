using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// PostgreSQL is the only data store (NFR-DAT-1). No table in it is partitioned (NFR-DAT-4), and
/// operational logs are not written to it (NFR-DAT-7). The packages that could add another store
/// or another log output are guarded by the architecture tests.
/// </summary>
public class SingleStoreTests
{
    [Fact]
    public void The_applications_database_is_PostgreSQL()
    {
        using var application = new EcnanibApplicationFactory();
        using var context = application.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContext();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        Assert.StartsWith(
            "PostgreSQL ",
            context.Database.SqlQueryRaw<string>("""SELECT version() AS "Value" """).AsEnumerable().Single());
    }

    [Fact]
    public async Task The_database_holds_the_model_and_its_migration_history_and_no_log()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        // The application starts, serves a request, and logs: to its file, as configured.
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        application.Logger().LogWarning("Written {Marker}", "to the log file");

        Assert.Contains(application.Events(), written => written.Text("Marker") == "to the log file");

        using var context = database.Context();
        var modelTables = context.Model.GetEntityTypes().Select(entity => entity.GetTableName()!).Distinct().ToList();

        Assert.Equal(["ai_providers", "background_jobs", "prompt_templates", "roles"], modelTables.Order(StringComparer.Ordinal));
        Assert.Equal(
            modelTables.Append("__EFMigrationsHistory").Order(StringComparer.Ordinal),
            database.Tables());
    }

    [Fact]
    public void No_table_is_partitioned()
    {
        var database = PostgresServer.SharedDatabase;

        Assert.NotEmpty(database.Tables());
        Assert.Equal(0L, database.Scalar<long>("SELECT count(*) FROM pg_partitioned_table"));
        Assert.Equal(0L, database.Scalar<long>("SELECT count(*) FROM pg_inherits"));
    }
}
