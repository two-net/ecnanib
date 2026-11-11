using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Ecnanib.Platform.Persistence;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Pending migrations are applied automatically at startup, before the application serves
/// requests or starts schedulers, under a database lock (NFR-DAT-3). The lock makes a second
/// instance safe (NFR-ARC-4).
/// </summary>
public class MigrationAtStartupTests
{
    private const string Waiting = "Waiting for another instance to finish migrating the database";
    private const string Applying = "Applying {MigrationCount} database migrations, from {FirstMigration} to {LastMigration}";
    private const string Migrated = "The database was migrated to {Migration}";
    private const string UpToDate = "The database is up to date at {Migration}";

    [Fact]
    public async Task An_empty_database_is_migrated_before_anything_is_scheduled_or_served()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services => services
                .AddSingleton<SeenAtStart>()
                .AddHostedService(provider => provider.GetRequiredService<SeenAtStart>())),
            database);

        // A hosted service stands for a scheduler: it is started with the host, like every one.
        var seen = application.Services.GetRequiredService<SeenAtStart>();
        using var client = application.CreateClient();
        using var health = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.NotEmpty(AllMigrations());
        Assert.Equal(AllMigrations(), seen.AppliedMigrations);
        Assert.Equal(AllMigrations(), database.AppliedMigrations());
        Assert.True(health.IsSuccessStatusCode);
    }

    [Fact]
    public void The_first_start_says_what_it_applied_and_logs_no_warning_or_error()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        _ = application.Services;

        var applying = Assert.Single(application.Events(), written => written.Template == Applying);
        Assert.Equal("Ecnanib.Platform.Persistence.DatabaseMigrator", applying.Area);
        Assert.Equal(AllMigrations().Count, applying["MigrationCount"].GetInt32());
        Assert.Equal(AllMigrations()[0], applying.Text("FirstMigration"));
        Assert.Equal(AllMigrations()[^1], applying.Text("LastMigration"));

        Assert.Single(application.Events(), written => written.Template == Migrated);

        // Not even from asking an empty database for its migration history.
        Assert.DoesNotContain(application.Events(), written => written.Level is "Warning" or "Error" or "Fatal");

        // The lock is released while the application runs on, not only when it stops. Nothing
        // here has used the database since, which would have released it by the way. The lock
        // the application does hold while it runs is another one: see BackgroundWorkLockTests.
        Assert.Empty(database.SessionsHolding(DatabaseMigration.LockKey));
    }

    [Fact]
    public void Starting_again_changes_nothing()
    {
        var database = PostgresServer.CreateDatabase();

        using (var first = new LoggedApplication(LoggedApplication.Settings(), database: database))
        {
            _ = first.Services;
        }

        using var second = new LoggedApplication(LoggedApplication.Settings(), database: database);
        _ = second.Services;

        Assert.Equal(AllMigrations(), database.AppliedMigrations());
        Assert.Single(second.Events(), written => written.Template == UpToDate);
        Assert.DoesNotContain(second.Events(), written => written.Template is Applying or Migrated);
    }

    [Fact]
    public void Every_command_of_a_migration_runs_on_a_session_that_holds_the_lock()
    {
        var database = PostgresServer.CreateDatabase();
        var witness = new LockWitness();
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services =>
                services.ConfigureDbContext<EcnanibDbContext>(options => options.AddInterceptors(witness))),
            database);

        _ = application.Services;

        // From creating the migration history to recording the migration in it.
        Assert.Contains(witness.Seen, seen => seen.Command.Contains("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\"", StringComparison.Ordinal));
        Assert.Contains(witness.Seen, seen => seen.Command.Contains("CREATE TABLE roles", StringComparison.Ordinal));
        Assert.Contains(witness.Seen, seen => seen.Command.Contains("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal));

        var unlocked = witness.Seen.Where(seen => !seen.HeldTheLock).Select(seen => seen.Command).ToList();
        Assert.True(
            unlocked.Count == 0,
            "These commands ran on a session that did not hold the migration lock:" +
            Environment.NewLine + string.Join(Environment.NewLine, unlocked));
    }

    [Fact]
    public async Task A_second_instance_waits_while_another_one_migrates()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(LoggedApplication.Settings(), database: database);

        // Another instance, as far as the database can tell: a session that holds the lock.
        // Closed before the application is disposed, so the application is never left waiting.
        using var other = database.Open();
        Lock(other, "pg_advisory_lock");

        var starting = Task.Run(() => application.Services, TestContext.Current.CancellationToken);

        await Wait.UntilAsync(() => Logged(application, Waiting), "the application waiting for the migration lock");

        Assert.False(starting.IsCompleted);
        Assert.Empty(database.AppliedMigrations());
        Assert.DoesNotContain("roles", database.Tables());

        Lock(other, "pg_advisory_unlock");
        await starting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(AllMigrations(), database.AppliedMigrations());
        Assert.Single(application.Events(), written => written.Template == Waiting);
    }

    [Fact]
    public async Task Two_instances_that_start_together_apply_each_migration_once()
    {
        var database = PostgresServer.CreateDatabase();
        using var one = new LoggedApplication(LoggedApplication.Settings(), database: database);
        using var two = new LoggedApplication(LoggedApplication.Settings(), database: database);

        // With a limit: an instance that kept the lock would leave the other one waiting for good.
        await Task.WhenAll(
                Task.Run(() => one.Services, TestContext.Current.CancellationToken),
                Task.Run(() => two.Services, TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(AllMigrations(), database.AppliedMigrations());

        // One of them applied the migrations, and the other found them applied.
        var applied = new[] { one, two }.Count(application =>
            application.Events().Any(written => written.Template == Migrated));
        Assert.Equal(1, applied);
    }

    [Fact]
    public void A_session_that_does_not_keep_the_lock_is_reported()
    {
        var database = PostgresServer.CreateDatabase();
        using var application = new LoggedApplication(
            LoggedApplication.Settings(),
            host => host.ConfigureServices(services =>
                services.ConfigureDbContext<EcnanibDbContext>(options => options.AddInterceptors(new LockLoser()))),
            database);

        // What a connection pooler in transaction mode does to a lock that belongs to a session:
        // by the time the application releases it, the session no longer holds it.
        _ = application.Services;

        var warning = Assert.Single(application.Events(), written => written.Level is "Warning" or "Error" or "Fatal");
        Assert.Equal("Warning", warning.Level);
        Assert.StartsWith("The migration lock was not held by the session that released it", warning.Template);
        Assert.Equal(AllMigrations(), database.AppliedMigrations());
    }

    /// <summary>The migrations in the application, in order.</summary>
    private static IReadOnlyList<string> AllMigrations()
    {
        using var context = ModelOnly.Context();

        return [.. context.Database.GetMigrations()];
    }

    private static void Lock(NpgsqlConnection session, string function)
    {
        using var command = new NpgsqlCommand($"SELECT {function}(@key)", session);
        command.Parameters.AddWithValue("key", DatabaseMigration.LockKey);
        command.ExecuteScalar();
    }

    /// <summary>Whether the application has written an event, read while it is still writing.</summary>
    private static bool Logged(LoggedApplication application, string template)
    {
        try
        {
            return application.Events().Any(written => written.Template == template);
        }
        catch (JsonException)
        {
            // A line that is only half written yet.
            return false;
        }
    }

    /// <summary>Records, as it starts, the migrations the database has by then.</summary>
    private sealed class SeenAtStart(IDbContextFactory<EcnanibDbContext> contexts) : IHostedService
    {
        public IReadOnlyList<string>? AppliedMigrations { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);

            AppliedMigrations = [.. await context.Database.GetAppliedMigrationsAsync(cancellationToken)];
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Gives the migration lock away during the migration, on the session that holds it.</summary>
    private sealed class LockLoser : DbCommandInterceptor
    {
        private int _lost;

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (eventData.CommandSource == CommandSource.Migrations && Interlocked.Exchange(ref _lost, 1) == 0)
            {
                using var unlock = command.Connection!.CreateCommand();
                unlock.CommandText = "SELECT pg_advisory_unlock(@key)";
                unlock.Transaction = command.Transaction;

                var key = unlock.CreateParameter();
                key.ParameterName = "key";
                key.Value = DatabaseMigration.LockKey;
                unlock.Parameters.Add(key);

                Assert.True(unlock.ExecuteScalar() is true, "The session did not hold the lock to begin with.");
            }

            return result;
        }
    }

    /// <summary>
    /// Asks the database, before each command of a migration, whether the session about to run
    /// it holds the migration lock.
    /// </summary>
    private sealed class LockWitness : DbCommandInterceptor
    {
        public ConcurrentQueue<(string Command, bool HeldTheLock)> Seen { get; } = new();

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Witness(command, eventData);

            return result;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Witness(command, eventData);

            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Witness(command, eventData);

            return result;
        }

        private void Witness(DbCommand command, CommandEventData eventData)
        {
            if (eventData.CommandSource != CommandSource.Migrations)
            {
                return;
            }

            // A lock taken with one 64-bit key shows as its two halves.
            using var question = command.Connection!.CreateCommand();
            question.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND granted AND pid = pg_backend_pid() AND objsubid = 1
                      AND classid::bigint = @high AND objid::bigint = @low)
                """;
            question.Transaction = command.Transaction;
            Add(question, "high", DatabaseMigration.LockKey >> 32);
            Add(question, "low", DatabaseMigration.LockKey & 0xFFFFFFFF);

            Seen.Enqueue((command.CommandText, question.ExecuteScalar() is true));
        }

        private static void Add(DbCommand command, string name, long value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
