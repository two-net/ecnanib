using System.Net;
using System.Net.Sockets;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// The start-up contract end to end, on the built application as a child process (NFR-ARCH-01, NFR-ARCH-03,
/// FR-CFG-06): whatever stops the start ends the process with exit code 1 and the reason on the console, with nothing
/// bound and no job started. These need no database; a loopback port nothing listens on stands in for one that cannot be
/// reached. <c>dotnet test -- --filter-not-trait "Category=Process"</c> leaves them out.
/// </summary>
[Trait("Category", "Process")]
public class StartupProcessTests
{
    /// <summary>Serilog's console token for Critical; the console sink writes every level to standard output.</summary>
    private const string Critical = "[FTL]";

    private const string Migrator = "Ecnanib.Database.Migrations.DatabaseMigrator";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnknownArgumentPrintsTheUsageOnStandardErrorAndExits1()
    {
        Require();
        using var dir = new TempDirectory();
        await using var app = ApplicationProcess.Start(dir.Path, "--bogus");

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        Assert.Contains(app.StandardError, line => line.Contains("Unknown argument '--bogus'.", StringComparison.Ordinal));
        Assert.Contains(app.StandardError, line => line.StartsWith("Usage: ecnanib [--config <path>]", StringComparison.Ordinal));
        Assert.Empty(app.StandardOutput);
    }

    /// <summary>Named with <c>--config</c>, or the default <c>ecnanib.json</c> of the current directory (FR-CFG-01).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMissingConfigurationFileIsReportedOnStandardErrorAndExits1(bool named)
    {
        Require();
        using var dir = new TempDirectory();
        var fileName = named ? "missing.json" : "ecnanib.json";
        await using var app = named
            ? ApplicationProcess.Start(dir.Path, "--config", Path.Combine(dir.Path, fileName))
            : ApplicationProcess.Start(dir.Path);

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        // The directory may be reported through its resolved path (macOS /private/var), so the name and the file are checked, not the prefix.
        var reported = Assert.Single(app.StandardError, line => line.StartsWith("Configuration file not found: ", StringComparison.Ordinal));
        Assert.Contains(Path.GetFileName(dir.Path) + Path.DirectorySeparatorChar + fileName + ".", reported, StringComparison.Ordinal);
        Assert.EndsWith("Create it or pass --config <path>.", reported, StringComparison.Ordinal);
        Assert.Empty(app.StandardOutput);
    }

    /// <summary>FR-CFG-06: every section is validated before anything connects; the failure is logged at Critical without a stack trace.</summary>
    [Fact]
    public async Task AnEmptyConnectionStringIsRefusedAtCriticalAndExits1()
    {
        Require();
        using var dir = new TempDirectory();
        var config = StartupConfig.Write(dir, FreePort.Next(), connectionString: string.Empty);
        await using var app = ApplicationProcess.Start(dir.Path, "--config", config);

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        var refusal = Assert.Single(app.StandardOutput, line => line.Contains(Critical, StringComparison.Ordinal));
        Assert.Contains($"{Critical} {Migrator}: Configuration is invalid; the application will not start: ", refusal, StringComparison.Ordinal);
        Assert.Contains("Database:ConnectionString is required.", refusal, StringComparison.Ordinal);
        AssertNeverListened(app);
        Assert.DoesNotContain(app.Output, line => line.StartsWith("   at ", StringComparison.Ordinal));
    }

    /// <summary>NFR-ARCH-03: a database that cannot be reached means the schema cannot be brought up to date, so the process refuses to start.</summary>
    [Fact]
    public async Task AnUnreachableDatabaseFailsTheMigrationAtCriticalAndExits1WithoutListening()
    {
        Require();
        using var dir = new TempDirectory();
        var databasePort = FreePort.Next();
        var port = FreePort.Next();
        var config = StartupConfig.Write(dir, port, $"Host=127.0.0.1;Port={databasePort};Database=ecnanib_unreachable;Username=ecnanib;Password=x;Timeout=3");
        await using var app = ApplicationProcess.Start(dir.Path, "--config", config);

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        var refusal = Assert.Single(app.StandardOutput, line => line.Contains(Critical, StringComparison.Ordinal));
        Assert.Contains($"{Critical} {Migrator}: Database migration failed; the application will not start (NFR-ARCH-03): Database migration failed: ", refusal, StringComparison.Ordinal);
        Assert.Contains($"Failed to connect to 127.0.0.1:{databasePort}", refusal, StringComparison.Ordinal);
        AssertNeverListened(app);
        await AssertRefusedAsync(port);
    }

    internal static void Require()
    {
        if (!ApplicationProcess.IsAvailable)
            Assert.Skip(ApplicationProcess.SkipMessage);
    }

    /// <summary>Neither the application's own started line nor Kestrel's appeared: no port was bound.</summary>
    internal static void AssertNeverListened(ApplicationProcess app) =>
        Assert.DoesNotContain(app.Output, line => line.Contains("started; listening on", StringComparison.Ordinal) || line.Contains("Now listening on", StringComparison.Ordinal));

    internal static async Task AssertRefusedAsync(int port)
    {
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port, Ct).AsTask());
    }
}
