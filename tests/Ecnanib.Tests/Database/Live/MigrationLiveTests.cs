using Ecnanib.Database.Migrations;
using Ecnanib.Tests.Database.Migrations;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ecnanib.Database;
using Npgsql;

namespace Ecnanib.Tests.Database.Live;

[Trait("Category", "Live")]
public class MigrationLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public MigrationLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppliesTheSchemaIdempotentlyAndRefusesADatabaseAheadOfTheBuild()
    {
        _fixture.Require();
        var logs = new TestLoggerProvider();
        await using var services = _fixture.BuildServices(logs);
        var migrator = services.GetRequiredService<DatabaseMigrator>();

        await migrator.MigrateAsync(Ct);

        Assert.Contains(logs.Entries, e => e.Message.StartsWith("Connected to PostgreSQL"));
        Assert.Contains(logs.Entries, e => e.Message.StartsWith("Applying 4 database migration"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("Applied database migration") && e.Message.Contains("InitialSchema"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("Applied database migration") && e.Message.Contains("AddDecisionPartialFraction"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("Applied database migration") && e.Message.Contains("ExtendExperienceMetrics"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("Applied database migration") && e.Message.Contains("AddExecutionColumns"));

        await using var db = _fixture.CreateContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Equal(0L, await _fixture.ScalarAsync<long>("SELECT count(*) FROM pg_partitioned_table"));
        Assert.Equal(MigrationsTests.ExpectedTables.Length, await _fixture.ScalarAsync<int>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'"));
        Assert.Equal("text", await _fixture.ScalarAsync<string>("SELECT data_type FROM information_schema.columns WHERE table_name = 'agents' AND column_name = 'state'"));
        Assert.Equal("jsonb", await _fixture.ScalarAsync<string>("SELECT data_type FROM information_schema.columns WHERE table_name = 'agents' AND column_name = 'parameters'"));
        Assert.Equal("timestamp with time zone", await _fixture.ScalarAsync<string>("SELECT data_type FROM information_schema.columns WHERE table_name = 'agents' AND column_name = 'created_at'"));

        var before = logs.Entries.Count;
        await migrator.MigrateAsync(Ct);
        Assert.Contains(logs.Entries.Skip(before), e => e.Message.Contains("up to date"));

        // DB-02: a database that knows a migration this build does not is refused, never reverted.
        const string future = "99991231235959_FromTheFuture";
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(future, "99.0.0")), Ct);
        try
        {
            var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() => migrator.MigrateAsync(Ct));
            Assert.Contains(future, ex.Message);
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains(future));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(history.GetDeleteScript(future), Ct);
        }
    }

    [Fact]
    public async Task AFailingMigrationIsLoggedAtCriticalAndRefusesToStart()
    {
        _fixture.Require();
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "ecnanib_test_fail_" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString;
        var logs = new TestLoggerProvider();
        await using var services = DatabaseTestHost.BuildServices(connectionString, logs);
        await using var db = services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContext();
        try
        {
            await db.GetService<IRelationalDatabaseCreator>().CreateAsync(Ct);
            // A table the migration also creates makes it fail part-way; the transaction rolls everything back.
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE users (id uuid PRIMARY KEY)", Ct);

            var ex = await Assert.ThrowsAsync<DatabaseMigrationException>(() => services.GetRequiredService<DatabaseMigrator>().MigrateAsync(Ct));

            Assert.Contains("InitialSchema", ex.Message);
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("will not start"));
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync(Ct));
            Assert.Equal(1, await _fixture.ScalarAsync<int>("SELECT 1"));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(Ct);
        }
    }

    [Fact]
    public async Task CreatesAMissingDatabaseBeforeMigrating()
    {
        _fixture.Require();
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "ecnanib_test_auto_" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString;
        var logs = new TestLoggerProvider();
        await using var services = DatabaseTestHost.BuildServices(connectionString, logs);
        await using var db = services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>().CreateDbContext();
        try
        {
            Assert.False(await db.Database.CanConnectAsync(Ct));

            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(Ct);

            Assert.Contains(logs.Entries, e => e.Message.Contains("does not exist; creating it"));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(Ct);
        }
    }
}
