using System.Collections.Concurrent;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Persistence;

/// <summary>AC-07: first start, upgrade, and the ways startup must fail.</summary>
public sealed class DatabaseStartupTests(PostgresFixture postgres)
{
    private const string ProviderNames = "SELECT name FROM providers ORDER BY name";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_start_creates_the_database_schema_and_seed_data()
    {
        var database = postgres.NewConnectionString();

        await StartAsync(database);

        Assert.Equal(
            ["Anthropic", "Google", "OpenAI", "xAI"],
            await Sql.ColumnAsync(database, ProviderNames, Cancellation));
    }

    /// <summary>NFR-MNT-02: a provider is its adapter, so one that only an adapter names still gets its row.</summary>
    [Fact]
    public async Task Every_provider_with_an_adapter_is_seeded()
    {
        var database = postgres.NewConnectionString();
        await using var app = new AppFactory(
            database,
            services => Ai.AiHarness.Use(services, new Ai.FakeAiProvider(), new Ai.TestAdapter("Test")),
            Ai.AiHarness.Settings());

        app.Start();

        Assert.Equal(
            ["Anthropic", "Google", "OpenAI", "Test", "xAI"],
            await Sql.ColumnAsync(database, ProviderNames, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_start_logs_no_errors(bool databaseExists)
    {
        var database = databaseExists
            ? await postgres.CreateDatabaseAsync(Cancellation)
            : postgres.NewConnectionString();
        var errors = new ErrorLog();

        await StartAsync(database, services => services.AddSingleton<ILoggerProvider>(errors));

        Assert.Empty(errors.Messages);
    }

    [Fact]
    public async Task First_start_works_on_an_existing_empty_database()
    {
        var database = await postgres.CreateDatabaseAsync(Cancellation);

        await StartAsync(database);

        Assert.Equal(4, await ProviderCountAsync(database));
    }

    [Fact]
    public async Task The_schema_has_no_partitioned_tables()
    {
        var database = postgres.NewConnectionString();

        await StartAsync(database);

        var partitioned = await Sql.ScalarAsync<long>(
            database,
            "SELECT count(*) FROM pg_class WHERE relkind IN ('p', 'I')",
            Cancellation);

        Assert.Equal(0, partitioned);
    }

    [Fact]
    public async Task A_restart_changes_nothing_and_keeps_administrator_edits()
    {
        var database = postgres.NewConnectionString();
        await StartAsync(database);
        await Sql.ExecuteAsync(database, "UPDATE providers SET enabled = false WHERE name = 'xAI'", Cancellation);
        var migrations = await Sql.ScalarAsync<long>(database, Sql.MigrationHistoryCount, Cancellation);

        await StartAsync(database);

        Assert.Equal(4, await ProviderCountAsync(database));
        Assert.Equal(migrations, await Sql.ScalarAsync<long>(database, Sql.MigrationHistoryCount, Cancellation));
        Assert.False(await Sql.ScalarAsync<bool>(database, "SELECT enabled FROM providers WHERE name = 'xAI'", Cancellation));
    }

    [Fact]
    public async Task Seeders_run_with_no_migration_pending_so_an_upgrade_can_add_reference_data()
    {
        var database = postgres.NewConnectionString();
        await StartAsync(database);
        await Sql.ExecuteAsync(database, "DELETE FROM providers WHERE name = 'xAI'", Cancellation);

        await StartAsync(database);

        Assert.Equal(4, await ProviderCountAsync(database));
    }

    [Fact]
    public async Task An_upgrade_applies_pending_migrations_and_keeps_existing_data()
    {
        var database = postgres.NewConnectionString();
        await StartAsync(database);
        await Sql.ExecuteAsync(database, "UPDATE providers SET enabled = false WHERE name = 'xAI'", Cancellation);

        await StartAsync(database, MigrationsWithUpgradeProbe.AddTo);

        Assert.Equal(
            1,
            await Sql.ScalarAsync<long>(
                database,
                $"""SELECT count(*) FROM "__EFMigrationsHistory" WHERE migration_id = '{UpgradeProbe.Id}'""",
                Cancellation));
        Assert.Equal(0, await Sql.ScalarAsync<long>(database, "SELECT count(*) FROM upgrade_probe", Cancellation));
        Assert.Equal(4, await ProviderCountAsync(database));
        Assert.False(await Sql.ScalarAsync<bool>(database, "SELECT enabled FROM providers WHERE name = 'xAI'", Cancellation));
    }

    [Fact]
    public async Task An_older_build_refuses_a_database_with_newer_migrations()
    {
        var database = postgres.NewConnectionString();
        await StartAsync(database, MigrationsWithUpgradeProbe.AddTo);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(database));

        Assert.Contains(UpgradeProbe.Id, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_seeder_prevents_startup_saves_nothing_and_recovers_on_the_next_start()
    {
        var database = postgres.NewConnectionString();

        await Assert.ThrowsAsync<SeedFailedException>(() =>
            StartAsync(database, services => services.AddSingleton<IReferenceDataSeeder, FailingSeeder>()));

        Assert.Equal(0, await ProviderCountAsync(database));

        await StartAsync(database);

        Assert.Equal(4, await ProviderCountAsync(database));
    }

    [Fact]
    public async Task A_missing_connection_string_prevents_startup()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(connectionString: null));

        Assert.Contains("ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_context_cannot_be_injected_because_a_circuit_scope_would_outlive_it()
    {
        await using var app = new AppFactory(postgres.NewConnectionString());
        app.Start();
        await using var scope = app.Services.CreateAsyncScope();

        Assert.Null(scope.ServiceProvider.GetService<EcnanibDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetService<IDbContextFactory<EcnanibDbContext>>());
    }

    private static async Task StartAsync(string? connectionString, Action<IServiceCollection>? configureServices = null)
    {
        await using var app = new AppFactory(connectionString, configureServices);
        app.Start();
    }

    private static Task<long> ProviderCountAsync(string database) =>
        Sql.ScalarAsync<long>(database, "SELECT count(*) FROM providers", Cancellation);

    private sealed class ErrorLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class SeedFailedException : Exception;

    private sealed class FailingSeeder : IReferenceDataSeeder
    {
        public Task SeedAsync(EcnanibDbContext context, CancellationToken cancellationToken) =>
            throw new SeedFailedException();
    }
}
