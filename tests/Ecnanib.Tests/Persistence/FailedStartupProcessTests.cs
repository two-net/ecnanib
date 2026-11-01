using System.Diagnostics;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// NFR-DEP-03 end to end. This runs the real process rather than a test host, so the exit code and
/// console output are the ones an operator sees.
/// </summary>
public sealed class FailedStartupProcessTests(PostgresFixture postgres)
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

    private static async Task<(int ExitCode, string Output)> RunApplicationAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // An empty working directory is an empty content root, so no settings file is loaded.
        var workingDirectory = Directory.CreateTempSubdirectory("ecnanib-tests-");

        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "exec",
                    typeof(Program).Assembly.Location,
                    $"--ConnectionStrings:Default={connectionString}",
                    "--urls=http://127.0.0.1:0",
                },
                Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = workingDirectory.FullName,
            })!;

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
