using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// The PostgreSQL server the tests run the application against: one container for the whole test
/// process, started the first time a test needs it and removed when the process ends. The tests
/// need Docker for it.
/// </summary>
internal static class PostgresServer
{
    private const string Image = "postgres:18";

    // The user and the password of SettingsJson.Valid(), so that a file pointed at this server
    // still carries the password that the masking tests look for.
    private const string User = "app";
    private const string Password = "database-password";

    // The shared memory of the container, in bytes. PostgreSQL keeps the statistics of every
    // database there, and a database of a test stays until the process ends. A container has
    // 64 megabytes of it unless it is given more, which the databases of one run outgrow.
    private const long SharedMemory = 512L * 1024 * 1024;

    private static readonly Lazy<PostgreSqlContainer> Container = new(Start);

    private static readonly Lazy<TestDatabase> Shared = new(() =>
    {
        var database = new TestDatabase(ConnectionStringTo("ecnanib"));

        // Migrated here, once, so that no test meets the first start by chance. The tests of
        // the first start use a database of their own.
        database.Migrate();

        return database;
    });

    private static readonly Lazy<ServerTools> Tools = new(() => ServerTools.In(Container.Value.Id));

    /// <summary>
    /// One database, already migrated, for every test that needs the application to start and
    /// has nothing to say about the database.
    /// </summary>
    public static TestDatabase SharedDatabase => Shared.Value;

    /// <summary>A new, empty database for one test.</summary>
    public static TestDatabase CreateDatabase()
    {
        var name = $"test_{Guid.NewGuid():N}";
        SharedDatabase.Execute($"""CREATE DATABASE "{name}" """);

        return new TestDatabase(ConnectionStringTo(name));
    }

    /// <summary>
    /// The tool that backs a database of this server up, as the <c>Backup:PgDump</c> of an
    /// application of the tests names it: see <see cref="ServerTools"/>.
    /// </summary>
    public static string PgDump => Tools.Value.PgDump;

    /// <summary>The tool that restores a backup to a database of this server, named in the same way.</summary>
    public static string PgRestore => Tools.Value.PgRestore;

    /// <summary>A connection string to a database of this server, whether it exists or not.</summary>
    public static string ConnectionStringTo(string database) =>
        new NpgsqlConnectionStringBuilder(Container.Value.GetConnectionString()) { Database = database }.ConnectionString;

    private static PostgreSqlContainer Start()
    {
        var container = new PostgreSqlBuilder(Image)
            .WithDatabase("ecnanib")
            .WithUsername(User)
            .WithPassword(Password)

            // Test classes run side by side, and each application keeps its own connections.
            .WithCommand("-c", "max_connections=300")
            .WithCreateParameterModifier(parameters => (parameters.HostConfig ??= new()).ShmSize = SharedMemory)

            // The container library writes to the console, which some tests read.
            .WithLogger(NullLogger.Instance)
            .Build();

        // On a thread of its own: a test's thread must not be what the start waits to come back to.
        Task.Run(() => container.StartAsync()).GetAwaiter().GetResult();

        return container;
    }
}

/// <summary>
/// The client tools of PostgreSQL that the application backs a database up and restores it with,
/// for the tests: two scripts that run <c>pg_dump</c> and <c>pg_restore</c> inside the container
/// of the test server, where they are. So the tests need Docker, with its command, and no
/// PostgreSQL on the machine that runs them.
/// </summary>
/// <remarks>
/// The application tells a tool where the server is in the variables of the tool's process, and
/// hands it what it reads and takes what it writes over its standard input and output. So a
/// script passes the user and the password on as they are, says the address that the server has
/// inside its container, and needs no file to be in both places.
/// </remarks>
internal sealed class ServerTools
{
    private ServerTools(string pgDump, string pgRestore)
    {
        PgDump = pgDump;
        PgRestore = pgRestore;
    }

    public string PgDump { get; }

    public string PgRestore { get; }

    public static ServerTools In(string container)
    {
        var folder = Directory.CreateTempSubdirectory("ecnanib-tests-tools-").FullName;

        // Removed with the process, as the container is.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(folder, recursive: true);

        return new ServerTools(
            Script(folder, "pg_dump", Inside(container, "pg_dump")),
            Script(folder, "pg_restore", Inside(container, "pg_restore")));
    }

    /// <summary>Writes a script that the application can run as a tool, and says where it is.</summary>
    /// <param name="folder">Where the script goes.</param>
    /// <param name="name">The name of its file.</param>
    /// <param name="command">What it does, as a line of the shell.</param>
    public static string Script(string folder, string name, string command)
    {
        var path = Path.Combine(folder, name);

        File.WriteAllText(path, $"#!/bin/sh\n{command}\n");

        // A script of the shell: the tests of the backups run where one runs.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string Inside(string container, string tool) =>
        $"exec docker exec -i -e PGHOST=localhost -e PGPORT=5432 -e PGUSER -e PGPASSWORD {container} {tool} \"$@\"";
}

/// <summary>A database of the test server, and what a test wants to ask it directly.</summary>
internal sealed class TestDatabase(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public string Name { get; } = new NpgsqlConnectionStringBuilder(connectionString).Database!;

    /// <summary>
    /// The application's context on this database, outside the application: what the migrations
    /// and the model do on their own.
    /// </summary>
    public EcnanibDbContext Context() =>
        new(new DbContextOptionsBuilder<EcnanibDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Applies the application's migrations, and nothing else of the application.</summary>
    public void Migrate()
    {
        using var context = Context();
        context.Database.Migrate();
    }

    /// <summary>A session of the test's own, open until it is disposed.</summary>
    public NpgsqlConnection Open()
    {
        // Not from a pool: closing it ends the session, and with it whatever the session holds.
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
        connection.Open();

        return connection;
    }

    public void Execute(string sql)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(sql, connection);

        return (T)command.ExecuteScalar()!;
    }

    /// <summary>The values of the first column of a query, as text.</summary>
    public IReadOnlyList<string> Column(string sql)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();

        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// The sessions of this database that hold the advisory lock with a key, by their process
    /// ids. A lock taken with one 64-bit key shows as its two halves.
    /// </summary>
    public IReadOnlyList<int> SessionsHolding(long key)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            """
            SELECT pid FROM pg_locks
            WHERE locktype = 'advisory' AND granted AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND classid::bigint = @high AND objid::bigint = @low
            """,
            connection);
        command.Parameters.AddWithValue("high", key >> 32);
        command.Parameters.AddWithValue("low", key & 0xFFFFFFFF);
        using var reader = command.ExecuteReader();

        var sessions = new List<int>();
        while (reader.Read())
        {
            sessions.Add(reader.GetInt32(0));
        }

        return sessions;
    }

    /// <summary>The names of the tables in the database, in order.</summary>
    public IReadOnlyList<string> Tables() =>
    [
        .. Column("SELECT tablename::text FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema')")
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The migrations the database records as applied, in order.</summary>
    public IReadOnlyList<string> AppliedMigrations() =>
        Tables().Contains("__EFMigrationsHistory")
            ? Column("""SELECT migration_id::text FROM "__EFMigrationsHistory" ORDER BY migration_id""")
            : [];
}
