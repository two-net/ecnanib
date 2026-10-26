using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Database;
using Ecnanib.Database.Migrations;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// NFR-ARCH-01 and NFR-ARCH-03 against PostgreSQL on the built application as a child process: a migration that fails
/// ends the start with exit code 1 and nothing bound; a start that succeeds creates and migrates the database, takes
/// its instance lock, serves, refuses a second process on the same database and stops cleanly on SIGTERM. Each test
/// uses a throwaway database of its own and drops it afterwards. Both <c>Live</c> and <c>Process</c>, so
/// <c>--filter-not-trait "Category=Process"</c> spawns no child process at all.
/// </summary>
[Trait("Category", "Live")]
[Trait("Category", "Process")]
public class StartupProcessLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private const string Critical = "[FTL]";

    private readonly DatabaseLiveFixture _fixture;

    public StartupProcessLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFailingMigrationRefusesToStartLeavesNoHistoryAndNeverListens()
    {
        Require();
        var (testConnectionString, applicationConnectionString, _) = ThrowawayDatabase("fail");
        using var dir = new TempDirectory();
        await using var db = EcnanibDbContextDesignTimeFactory.Create(testConnectionString);
        try
        {
            await db.GetService<IRelationalDatabaseCreator>().CreateAsync(Ct);
            // A table the first migration also creates makes it fail part-way; the transaction rolls everything back.
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE users (id uuid PRIMARY KEY)", Ct);
            var port = FreePort.Next();
            await using var app = ApplicationProcess.Start(dir.Path, "--config", StartupConfig.Write(dir, port, applicationConnectionString));

            Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

            Assert.Contains(app.StandardOutput, line => Regex.IsMatch(line, @"Applying \d+ database migration"));
            var refusal = Assert.Single(app.StandardOutput, line => line.Contains(Critical, StringComparison.Ordinal));
            Assert.Matches(@"\[FTL\] Ecnanib\.Database\.Migrations\.DatabaseMigrator: Database migration failed; the application will not start \(NFR-ARCH-03\): Migration '\d+_InitialSchema' failed: ", refusal);
            StartupProcessTests.AssertNeverListened(app);
            await StartupProcessTests.AssertRefusedAsync(port);
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync(Ct);
        }
    }

    [Fact]
    public async Task TheApplicationStartsMigratesLocksServesAndStopsCleanlyOnSigterm()
    {
        Require();
        var (testConnectionString, applicationConnectionString, name) = ThrowawayDatabase("proc");
        using var dir = new TempDirectory();
        using var secondDir = new TempDirectory();
        await using var db = EcnanibDbContextDesignTimeFactory.Create(testConnectionString);
        ApplicationProcess? first = null;
        ApplicationProcess? second = null;
        try
        {
            var port = FreePort.Next();
            var secondPort = FreePort.Next();
            first = ApplicationProcess.Start(dir.Path, "--config", StartupConfig.Write(dir, port, applicationConnectionString));

            await first.WaitForLineAsync(new Regex($@"Ecnanib \S+ started; listening on 127\.0\.0\.1:{port}$"), ApplicationProcess.StartTimeout);

            // Created, migrated, locked, keyed: the start-up sequence of the database and configuration READMEs.
            Assert.Contains(first.StandardOutput, line => line.Contains("does not exist; creating it", StringComparison.Ordinal));
            Assert.Contains(first.StandardOutput, line => line.Contains("Connected to PostgreSQL", StringComparison.Ordinal));
            Assert.Contains(first.StandardOutput, line => Regex.IsMatch(line, @"Applying \d+ database migration"));
            var locked = Assert.Single(first.StandardOutput, line => line.Contains("Instance lock on database", StringComparison.Ordinal) && line.Contains("acquired as backend", StringComparison.Ordinal));
            var backend = int.Parse(Regex.Match(locked, @"acquired as backend (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(first.StandardOutput, line => line.Contains("Generated the Data Protection key and stored it in the database (FR-CFG-05)", StringComparison.Ordinal));
            Assert.DoesNotContain(first.Output, line => line.Contains(Critical, StringComparison.Ordinal));
            Assert.Equal(db.Database.GetMigrations().Order(StringComparer.Ordinal), (await db.Database.GetAppliedMigrationsAsync(Ct)).Order(StringComparer.Ordinal));
            Assert.Equal(1, await HeldLocksAsync(name));
            Assert.Equal(backend, await LockHolderAsync(name));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using (var landing = await http.GetAsync($"http://127.0.0.1:{port}/", Ct))
            {
                Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
                Assert.Contains("Ecnanib", await landing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }

            // NFR-ARCH-01, NFR-REL-04: a second process on the same database is refused before it binds its port.
            second = ApplicationProcess.Start(secondDir.Path, "--config", StartupConfig.Write(secondDir, secondPort, applicationConnectionString));
            Assert.Equal(1, await second.WaitForExitAsync(ApplicationProcess.StartTimeout));
            Assert.Contains(second.StandardOutput, line => line.Contains("Database schema is up to date", StringComparison.Ordinal));
            var refusal = Assert.Single(second.StandardOutput, line => line.Contains(Critical, StringComparison.Ordinal));
            Assert.Matches($@"\[FTL\] Ecnanib\.Database\.InstanceLock: Another Ecnanib instance holds database {name} on \S+ \(backend {backend}\); this instance will not start \(NFR-REL-04\)", refusal);
            StartupProcessTests.AssertNeverListened(second);
            await StartupProcessTests.AssertRefusedAsync(secondPort);
            Assert.Equal(1, await HeldLocksAsync(name));
            using (var stillServing = await http.GetAsync($"http://127.0.0.1:{port}/", Ct))
                Assert.Equal(HttpStatusCode.OK, stillServing.StatusCode);

            // SIGTERM, as a service manager stops it: a clean stop, the lock released, the port closed.
            var exit = await first.StopAsync(ApplicationProcess.ExitTimeout);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(0, exit);
                Assert.Contains(first.StandardOutput, line => line.Contains("Ecnanib is stopping", StringComparison.Ordinal));
                Assert.Contains(first.StandardOutput, line => line.Contains("Instance lock released (NFR-REL-04)", StringComparison.Ordinal));
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (await HeldLocksAsync(name) > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100, Ct);
            Assert.Equal(0, await HeldLocksAsync(name));
            await StartupProcessTests.AssertRefusedAsync(port);
        }
        finally
        {
            if (first is not null)
                await first.DisposeAsync();
            if (second is not null)
                await second.DisposeAsync();
            await db.Database.EnsureDeletedAsync(Ct);
        }
    }

    private void Require()
    {
        _fixture.Require();
        StartupProcessTests.Require();
    }

    /// <summary>A database of this test's own: the fixture's server, the test's contexts unpooled, the application's pooled as in production.</summary>
    private (string TestConnectionString, string ApplicationConnectionString, string Name) ThrowawayDatabase(string purpose)
    {
        var name = $"ecnanib_test_{purpose}_{Guid.NewGuid().ToString("N")[..8]}";
        var test = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name };
        var application = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name, Pooling = true };
        return (test.ConnectionString, application.ConnectionString, name);
    }

    /// <summary>The instance lock's advisory key, in the named database only; <c>pg_locks</c> is cluster-wide and the other live tests hold the same key in theirs.</summary>
    private static string LockQuery(string select, string database) =>
        $"SELECT {select} FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND classid = {(int)(InstanceLock.Key >> 32)} AND objid = {(int)(InstanceLock.Key & 0xFFFFFFFF)} AND database = (SELECT oid FROM pg_database WHERE datname = '{database}')";

    private Task<long> HeldLocksAsync(string database) => _fixture.ScalarAsync<long>(LockQuery("count(*)", database));

    private Task<int> LockHolderAsync(string database) => _fixture.ScalarAsync<int>(LockQuery("pid", database));
}
