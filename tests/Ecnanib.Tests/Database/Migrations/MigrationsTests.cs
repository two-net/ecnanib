using System.Data.Common;
using System.Net.Sockets;
using Ecnanib.Database;
using Ecnanib.Database.Configuration;
using Ecnanib.Database.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Ecnanib.Tests.Database.Migrations;

/// <summary>Checks the migrations assembly against the model without a database (DB-02, DB-03).</summary>
public class MigrationsTests
{
    public static readonly string[] ExpectedTables =
    [
        "agent_changes", "agents", "candles", "council_assignments", "data_protection_keys", "decision_role_outputs",
        "decisions", "deliberation_transcripts", "disclaimer_acceptances", "entry_windows", "evaluations",
        "exchange_keys", "experience_stats", "log_entries", "orders", "provider_keys", "provider_models",
        "provider_sync_states", "shadow_outcomes", "system_keys", "trades", "users",
    ];

    [Fact]
    public void ModelMatchesTheLatestMigration()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        Assert.False(db.Database.HasPendingModelChanges(), "Add a migration: the model differs from the snapshot (DB-02).");
    }

    [Fact]
    public void MigrationsCreateEveryTableAndNothingIsPartitioned()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.DoesNotContain("PARTITION", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NULLS NOT DISTINCT", script);
        foreach (var table in ExpectedTables)
            Assert.Contains($"CREATE TABLE {table} (", script);
    }

    [Fact]
    public void ModelContainsExactlyTheExpectedTables()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();

        var tables = db.Model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            .Select(e => e.GetTableName())
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedTables, tables);
    }

    [Fact]
    public void EveryMigrationIsForwardOnly()
    {
        using var db = EcnanibDbContextDesignTimeFactory.Create();
        var assembly = db.GetService<IMigrationsAssembly>();
        Assert.NotEmpty(assembly.Migrations);

        foreach (var (id, type) in assembly.Migrations)
        {
            var migration = assembly.CreateMigration(type, db.Database.ProviderName!);
            Assert.NotEmpty(migration.UpOperations);
            var ex = Assert.Throws<NotSupportedException>(() => migration.DownOperations);
            Assert.Contains("forward-only", ex.Message);
            Assert.Matches("^[0-9]{14}_", id);
        }
    }

    [Theory]
    [InlineData(15, 0, true)]
    [InlineData(17, 2, true)]
    [InlineData(14, 9, false)]
    [InlineData(13, 0, false)]
    public void RequiresPostgres15OrNewer(int major, int minor, bool accepted)
    {
        var version = new Version(major, minor);

        if (accepted)
        {
            DatabaseMigrator.RequireSupportedVersion(version);
        }
        else
        {
            var ex = Assert.Throws<DatabaseMigrationException>(() => DatabaseMigrator.RequireSupportedVersion(version));
            Assert.Contains("PostgreSQL 15.0 or newer", ex.Message);
        }
    }

    /// <summary>NFR-ARCH-03: the Critical line names the real cause, not only EF's wrapper around a refused connection.</summary>
    [Fact]
    public void DescribeNamesEveryDistinctCauseDownTheChain()
    {
        var refused = new InvalidOperationException("An exception has been raised that is likely due to a transient failure.",
            new NpgsqlException("Failed to connect to 127.0.0.1:1", new SocketException((int)SocketError.ConnectionRefused)));

        var described = DatabaseMigrator.Describe(refused);

        Assert.StartsWith("An exception has been raised that is likely due to a transient failure: Failed to connect to 127.0.0.1:1: ", described);
        Assert.Equal("one: two", DatabaseMigrator.Describe(new InvalidOperationException("one.", new InvalidOperationException("one", new InvalidOperationException("two.")))));
        Assert.Equal("alone", DatabaseMigrator.Describe(new InvalidOperationException("alone")));
        // An AggregateException quotes its cause in its own message; the cause is named once.
        var aggregate = new AggregateException(new NpgsqlException("Failed to connect to 127.0.0.1:1", new SocketException((int)SocketError.ConnectionRefused)));
        Assert.StartsWith("Failed to connect to 127.0.0.1:1: ", DatabaseMigrator.Describe(aggregate));
        Assert.DoesNotContain("One or more errors", DatabaseMigrator.Describe(aggregate));
    }

    [Fact]
    public void DesignTimeChecksNeverOpenAConnection()
    {
        var interceptor = new ConnectionSpy();
        var builder = new DbContextOptionsBuilder<EcnanibDbContext>().AddInterceptors(interceptor);
        NpgsqlConfiguration.Apply(builder, EcnanibDbContextDesignTimeFactory.PlaceholderConnectionString, new DatabaseOptions());
        using var db = new EcnanibDbContext(builder.Options);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", new EcnanibDbContextDesignTimeFactory().CreateDbContext([]).Database.ProviderName);

        _ = db.Database.HasPendingModelChanges();
        _ = db.GetService<IMigrator>().GenerateScript();
        _ = db.Model.GetEntityTypes().Count();

        Assert.False(interceptor.Opened, "A design-time check opened a database connection.");
    }

    private sealed class ConnectionSpy : DbConnectionInterceptor
    {
        public bool Opened { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Opened = true;
            return result;
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Opened = true;
            return ValueTask.FromResult(result);
        }
    }
}
