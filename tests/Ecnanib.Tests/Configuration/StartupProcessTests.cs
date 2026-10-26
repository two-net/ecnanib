using System.Net;
using System.Net.Sockets;
using Ecnanib.Configuration;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// The start-up contract end to end, on the built application as a child process (NFR-ARCH-01, NFR-ARCH-03,
/// FR-CFG-06): whatever stops the start ends the process with exit code 1 and the reason on the console, with nothing
/// bound and no job started. The process reads <c>appsettings.json</c> from its working directory and the standard
/// overrides (FR-CFG-01, FR-CFG-02). These need no database; a loopback port nothing listens on stands in for one that
/// cannot be reached. <c>dotnet test -- --filter-not-trait "Category=Process"</c> leaves them out.
/// </summary>
[Trait("Category", "Process")]
public class StartupProcessTests
{
    /// <summary>Serilog's console token for Critical; the console sink writes every level to standard output.</summary>
    private const string Critical = "[FTL]";

    private const string Migrator = "Ecnanib.Database.Migrations.DatabaseMigrator";

    /// <summary>Syntactically valid, never connected to: validation reads it, the migration would be the first to dial.</summary>
    private const string UnusedConnectionString = "Host=127.0.0.1;Port=1;Database=ecnanib_unused;Username=ecnanib;Password=x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>FR-CFG-06: a file that cannot be loaded, or one with a section nothing reads, is reported on standard error before anything is built.</summary>
    [Theory]
    [InlineData("{\n  \"Server\": {\n    \"Port\": 5207,,\n  }\n}\n", "It is not valid JSON at line 3, column 18:")]
    [InlineData("{ \"Server\": {}, \"ConnectionStrings\": { \"Default\": \"x\" } }", "has unknown top-level section(s): 'ConnectionStrings'.")]
    public async Task AnUnusableFileIsReportedOnStandardErrorAndExits1(string content, string expected)
    {
        Require();
        using var dir = new TempDirectory();
        dir.Write(ConfigurationFile.FileName, content);
        await using var app = ApplicationProcess.Start(dir.Path);

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        // The directory may be reported through its resolved path (macOS /private/var), so the name and the file are checked, not the prefix.
        var reported = Assert.Single(app.StandardError, line => line.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(Path.GetFileName(dir.Path) + Path.DirectorySeparatorChar + ConfigurationFile.FileName, reported, StringComparison.Ordinal);
        Assert.Empty(app.StandardOutput);
    }

    /// <summary>
    /// FR-CFG-06: every section is validated before anything connects; the failure is logged at Critical without a stack
    /// trace. Without a file the defaults apply (FR-CFG-01) and the connection string is missing all the same.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMissingConnectionStringIsRefusedAtCriticalAndExits1(bool fileExists)
    {
        Require();
        using var dir = new TempDirectory();
        if (fileExists)
            StartupConfig.Write(dir, FreePort.Next(), connectionString: string.Empty);
        await using var app = ApplicationProcess.Start(dir.Path);

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        var refusal = Assert.Single(app.StandardOutput, line => line.Contains(Critical, StringComparison.Ordinal));
        Assert.Contains($"{Critical} {Migrator}: Configuration is invalid; the application will not start: ", refusal, StringComparison.Ordinal);
        Assert.Contains("Database:ConnectionString is required.", refusal, StringComparison.Ordinal);
        AssertNeverListened(app);
        Assert.DoesNotContain(app.Output, line => line.StartsWith("   at ", StringComparison.Ordinal));
    }

    /// <summary>FR-CFG-02: the standard sources override the file, in the standard order: the environment, then the command line.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheEnvironmentAndTheCommandLineOverrideTheFile(bool viaEnvironment)
    {
        Require();
        using var dir = new TempDirectory();
        StartupConfig.Write(dir, FreePort.Next(), UnusedConnectionString);
        await using var app = viaEnvironment
            ? ApplicationProcess.Start(dir.Path, new Dictionary<string, string?> { ["Server__PublicUrl"] = "http://ecnanib.example.com" })
            : ApplicationProcess.Start(dir.Path, "--Server:PublicUrl=http://ecnanib.example.com");

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        var refusal = Assert.Single(app.StandardOutput, line => line.Contains(Critical, StringComparison.Ordinal));
        Assert.Contains($"{Critical} {Migrator}: Configuration is invalid; the application will not start: ", refusal, StringComparison.Ordinal);
        Assert.Contains("Server:PublicUrl must be https unless its host is localhost or a loopback address", refusal, StringComparison.Ordinal);
        AssertNeverListened(app);
    }

    /// <summary>FR-CFG-06: the Kestrel section is loaded before the database is touched; a URL Kestrel cannot parse is refused at Critical, not at bind time with a stack trace.</summary>
    [Fact]
    public async Task AnUnparseableListenUrlIsRefusedAtCriticalAndExits1()
    {
        Require();
        using var dir = new TempDirectory();
        StartupConfig.Write(dir, FreePort.Next(), UnusedConnectionString, listenUrl: "127.0.0.1:0:0");
        await using var app = ApplicationProcess.Start(dir.Path);

        Assert.Equal(1, await app.WaitForExitAsync(ApplicationProcess.StartTimeout));

        var refusal = Assert.Single(app.StandardOutput, line => line.Contains(Critical, StringComparison.Ordinal));
        Assert.Contains("The Kestrel section is invalid; the application will not start: ", refusal, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:0:0", refusal, StringComparison.Ordinal);
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
        StartupConfig.Write(dir, port, $"Host=127.0.0.1;Port={databasePort};Database=ecnanib_unreachable;Username=ecnanib;Password=x;Timeout=3");
        await using var app = ApplicationProcess.Start(dir.Path);

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
