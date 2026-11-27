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

        Assert.Equal(
            [
                "agent_roles", "agent_state_changes", "agents", "ai_credentials", "ai_models", "ai_providers",
                "analyst_calls", "audit_events", "background_jobs", "board_opt_outs", "catalog_sync_runs", "decision_cycles", "email_opt_ins",
                "equity_marks",
                "equity_snapshots", "exchange_credentials", "fills", "fundings", "legal_acceptances", "lessons", "notifications",
                "orders", "pending_sign_ins", "position_results", "position_roles", "positions", "prompt_templates", "restore_tests",
                "risk_check_rules", "risk_checks", "role_outputs", "roles", "runtime_flags", "sessions", "shadow_trades",
                "two_factor_recovery_codes", "users",
            ],
            modelTables.Order(StringComparer.Ordinal));
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
