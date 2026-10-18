using Ecnanib.Database;
using Ecnanib.Database.Migrations;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Database.Live;

/// <summary>
/// One throwaway database per test class: created empty on initialization, migrated on demand, dropped on dispose.
/// Does nothing when <see cref="DatabaseLiveTestConfig"/> is absent; tests then skip through <see cref="Require"/>.
/// </summary>
public sealed class DatabaseLiveFixture : IAsyncLifetime
{
    private string? _connectionString;
    private bool _migrated;

    public string ConnectionString => _connectionString ?? throw new InvalidOperationException("Call Require() first.");

    public async ValueTask InitializeAsync()
    {
        var configured = DatabaseLiveTestConfig.Load()?.ConnectionString;
        if (string.IsNullOrWhiteSpace(configured))
            return;

        var name = "ecnanib_test_" + Guid.NewGuid().ToString("N")[..12];
        _connectionString = new NpgsqlConnectionStringBuilder(configured) { Database = name, Pooling = false }.ConnectionString;
        await using var db = CreateContext();
        await db.GetService<IRelationalDatabaseCreator>().CreateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionString is null)
            return;

        NpgsqlConnection.ClearAllPools();
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }

    public void Require()
    {
        if (_connectionString is null)
            Assert.Skip(DatabaseLiveTestConfig.SkipMessage);
    }

    public EcnanibDbContext CreateContext() => EcnanibDbContextDesignTimeFactory.Create(ConnectionString);

    /// <summary>The module as the application registers it, pointing at this fixture's database.</summary>
    public ServiceProvider BuildServices(TestLoggerProvider? logs = null, TimeProvider? time = null, Dictionary<string, string?>? config = null) =>
        DatabaseTestHost.BuildServices(ConnectionString, logs, config, time);

    /// <summary>Applies the migrations once per fixture through the real startup migrator.</summary>
    public async Task MigrateAsync()
    {
        if (_migrated)
            return;

        await using var services = BuildServices();
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(TestContext.Current.CancellationToken);
        _migrated = true;
    }

    /// <summary>Reads one value from the catalog with plain Npgsql; the ORM-only rule (DB-01) binds the application, not its tests.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
