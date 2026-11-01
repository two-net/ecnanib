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

    public PostgresFixture() =>
        _container = new PostgreSqlBuilder("postgres:18-alpine").WithPassword(Password).Build();

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
}
