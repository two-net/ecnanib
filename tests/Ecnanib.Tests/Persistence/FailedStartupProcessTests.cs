using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// NFR-DEP-03 end to end. This runs the real process rather than a test host, so the exit code and
/// console output are the ones an operator sees.
/// </summary>
public sealed partial class FailedStartupProcessTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_failing_migration_stops_the_process_and_is_logged()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);
        await Sql.ExecuteAsync(database, "CREATE TABLE providers (clash integer)", cancellation);

        var (exitCode, output) = await RunApplicationAsync(database, cancellation);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("The application will not start", output, StringComparison.Ordinal);
        Assert.Contains("""relation "providers" already exists""", output, StringComparison.Ordinal);
        Assert.DoesNotContain(postgres.Password, output, StringComparison.Ordinal);
        Assert.Equal(0, await Sql.ScalarAsync<long>(database, Sql.MigrationHistoryCount, cancellation));
    }

    /// <summary>
    /// The history row puts the secret in the exception that stops startup, which goes to the log and
    /// then escapes to the runtime. The test above can't show this: its exception never holds a secret.
    /// </summary>
    [Fact]
    public async Task A_secret_in_a_startup_failure_is_masked_in_the_log_and_on_stderr()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);
        var canary = $"Z{Guid.NewGuid():N}";
        await Sql.ExecuteAsync(
            database,
            $"""
            CREATE TABLE "__EFMigrationsHistory" (
                migration_id character varying(150) PRIMARY KEY,
                product_version character varying(32) NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('password={canary}', '10.0.0');
            """,
            cancellation);

        var (exitCode, output) = await RunApplicationAsync(database, cancellation);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("The application will not start", output, StringComparison.Ordinal);
        Assert.Contains("password=***", output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// NFR-SEC-05 as an operator sees it. The message reaches stderr through the masking rules, which
    /// would hide a key's path after "EncryptionKeys:", so this proves the setting is still named.
    /// </summary>
    [Fact]
    public async Task The_sample_master_key_stops_the_process_before_any_migration_and_is_named()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);

        var (exitCode, output) = await RunApplicationAsync(
            database,
            cancellation,
            arguments: "--Security:EncryptionKeys:0:Key=<base64-encoded 256-bit key>");

        Assert.NotEqual(0, exitCode);
        Assert.Contains($"""Security:EncryptionKeys, entry 0 (Id "{AppFactory.TestKeyId}"), has the sample Key""", output, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await Sql.ScalarAsync<long>(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", cancellation));
    }

    /// <summary>
    /// CI probably runs in UTC, where a missing conversion wouldn't show, so the process gets a time
    /// zone of its own. Serilog stamps events with the local offset.
    /// </summary>
    [Fact]
    public async Task Timestamps_are_utc_in_a_process_that_isnt()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var database = await postgres.CreateDatabaseAsync(cancellation);
        await Sql.ExecuteAsync(database, "CREATE TABLE providers (clash integer)", cancellation);
        var logs = Directory.CreateTempSubdirectory("ecnanib-logs-");

        try
        {
            var (_, output) = await RunApplicationAsync(
                database,
                cancellation,
                new Dictionary<string, string?> { ["TZ"] = "Asia/Tokyo" },
                "--Logging:Sinks:File:Enabled=true",
                $"--Logging:Sinks:File:Path={Path.Combine(logs.FullName, "ecnanib-.log")}",
                "--Logging:Sinks:File:RetainedFileCount=1");

            var console = DateTimeOffset.ParseExact(
                ConsoleTimestamp().Match(output).Groups["timestamp"].Value,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);

            Assert.True((DateTimeOffset.UtcNow - console).Duration() < TimeSpan.FromMinutes(5), $"Console said {console:O}");

            var line = (await File.ReadAllLinesAsync(Assert.Single(logs.GetFiles()).FullName, cancellation))[0];
            var file = JsonDocument.Parse(line).RootElement.GetProperty("Timestamp").GetDateTimeOffset();

            Assert.Equal(TimeSpan.Zero, file.Offset);
        }
        finally
        {
            logs.Delete(recursive: true);
        }
    }

    [GeneratedRegex(@"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})Z \[", RegexOptions.Multiline)]
    private static partial Regex ConsoleTimestamp();

    private static async Task<(int ExitCode, string Output)> RunApplicationAsync(
        string connectionString,
        CancellationToken cancellationToken,
        Dictionary<string, string?>? environment = null,
        params string[] arguments)
    {
        // An empty working directory is an empty content root, so no settings file is loaded.
        var workingDirectory = Directory.CreateTempSubdirectory("ecnanib-tests-");

        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "exec",
                    typeof(Program).Assembly.Location,
                    $"--ConnectionStrings:Default={connectionString}",
                    "--Logging:MinimumLevel=Information",
                    $"--App:PublicBaseUrl={AppFactory.PublicBaseUrl}",
                    $"--Security:EncryptionKeys:0:Id={AppFactory.TestKeyId}",
                    $"--Security:EncryptionKeys:0:Key={AppFactory.TestKey}",
                    "--Security:EncryptionKeys:0:Active=true",
                    "--urls=http://127.0.0.1:0",
                },
                Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = workingDirectory.FullName,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var (name, value) in environment ?? [])
            {
                startInfo.Environment[name] = value;
            }

            using var process = Process.Start(startInfo)!;

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);

            // If startup wrongly succeeded, the application would run until it's stopped.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return (process.ExitCode, await output + await error);
        }
        finally
        {
            workingDirectory.Delete(recursive: true);
        }
    }
}
