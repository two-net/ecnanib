using Ecnanib.Tests.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// One PostgreSQL server for the whole test run. Tests stay independent by each using their own
/// database, which lets them run concurrently.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    /// <summary>
    /// Each test's database has its own connection pool, which stays open until it idles out, so the
    /// whole run adds up to far more than the server's default limit of 100.
    /// </summary>
    public PostgresFixture() =>
        _container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithPassword(Password)
            .WithCommand("-c", "max_connections=500")
            .Build();

    /// <summary>Unique, so a test can prove it never reaches a log.</summary>
    public string Password { get; } = $"canary-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>A connection string for a database that doesn't exist yet.</summary>
    public string NewConnectionString() =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"test_{Guid.NewGuid():N}",
        }.ConnectionString;

    /// <summary>A connection string for a database that exists and is empty.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connectionString = NewConnectionString();
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;

        await Sql.ExecuteAsync(_container.GetConnectionString(), $"""CREATE DATABASE "{name}" """, cancellationToken);

        return connectionString;
    }

    /// <summary>
    /// An outage of one database. The server is shared, so never stop or pause the container: that
    /// would take every concurrent test down with it.
    /// </summary>
    public async Task RefuseConnectionsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        var server = _container.GetConnectionString();

        await Sql.ExecuteAsync(server, $"""ALTER DATABASE "{name}" ALLOW_CONNECTIONS false""", cancellationToken);
        await Sql.ExecuteAsync(
            server,
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}'",
            cancellationToken);
    }

    /// <summary>Ends an outage started by <see cref="RefuseConnectionsAsync"/>.</summary>
    public Task AllowConnectionsAsync(string connectionString, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(
            _container.GetConnectionString(),
            $"""ALTER DATABASE "{new NpgsqlConnectionStringBuilder(connectionString).Database}" ALLOW_CONNECTIONS true""",
            cancellationToken);

    public Task DropDatabaseAsync(string connectionString, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(
            _container.GetConnectionString(),
            $"""DROP DATABASE "{new NpgsqlConnectionStringBuilder(connectionString).Database}" WITH (FORCE)""",
            cancellationToken);
}
