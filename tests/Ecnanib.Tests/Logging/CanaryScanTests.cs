using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Runs once: seeds every canary through the real application, stops it to flush, then keeps what
/// reached each real sink. This is the scan AC-03 asks for.
/// </summary>
public sealed class CanaryScan(PostgresFixture postgres) : IAsyncLifetime
{
    public const string Console = nameof(Console);
    public const string File = nameof(File);
    public const string Table = nameof(Table);

    public static readonly string[] Sinks = [Console, File, Table];

    private readonly Dictionary<string, string> _output = [];

    public const string WorkingDirectory = "/srv/ecnanib/current";

    public string ConfiguredSecret { get; } = CanaryCases.NewCanary();

    public string ConnectionPassword => postgres.Password;

    public string Output(string sink) => _output[sink];

    public static string Marker(string name) => $"marker{name.Replace(".", string.Empty, StringComparison.Ordinal)}x";

    public async ValueTask InitializeAsync()
    {
        var original = System.Console.Out;
        using var console = new StringWriter();
        var synchronized = TextWriter.Synchronized(console);
        System.Console.SetOut(synchronized);

        try
        {
            await using var app = new LoggedApp(
                postgres.NewConnectionString(),
                new Dictionary<string, string?>
                {
                    ["Logging:Sinks:Console:Enabled"] = "true",
                    ["Security:SessionSigningKey"] = ConfiguredSecret,

                    // Neither of these is a secret, though their names look like one.
                    ["PWD"] = WorkingDirectory,
                    ["Logging:Overrides:Microsoft.AspNetCore.Authorization"] = "Information",
                });

            var logger = app.Logger("Ecnanib.Tests.Canary");

            foreach (var canary in CanaryCases.All)
            {
                canary.Emit(logger, Marker(canary.Name));
            }

            EmitCases(logger);

            await app.StopAsync();

            _output[File] = app.FileText();
            _output[Table] = await app.TableTextAsync(TestContext.Current.CancellationToken);

            // Console.Out is shared, so applications in concurrent tests are still writing to it.
            // A synchronized writer locks on itself, and reading the text unlocked can throw.
            lock (synchronized)
            {
                _output[Console] = console.ToString();
            }
        }
        finally
        {
            System.Console.SetOut(original);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void EmitCases(ILogger logger)
    {
        // No rule knows these values. Only reading them from the configuration can mask them.
        logger.LogError("markerKnownProperty {Value}", ConfiguredSecret);
        logger.LogError("markerKnownPassword {Value}", postgres.Password);
        logger.LogError(new InvalidOperationException($"Failed on {postgres.Password}"), "markerKnownException");

        // Masking must not cost the content the logs exist for.
        logger.LogError("markerTokenCount {InputTokens} {OutputTokens} {TokenCount}", 1234, 5678, 6912);
        logger.LogError("markerTokenText tokens: {Used}", 4321);
        logger.LogError(new InvalidOperationException("""relation "providers" already exists"""), "markerException");
        logger.LogError("markerPath {Value}", $"{WorkingDirectory}/Ecnanib.dll");
        logger.LogError("markerLevelName {Value}", "Information");
    }
}

public sealed class CanaryScanTests(CanaryScan scan) : IClassFixture<CanaryScan>
{
    public static TheoryData<string, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string>();

            foreach (var sink in CanaryScan.Sinks)
            {
                foreach (var canary in CanaryCases.All)
                {
                    cases.Add(sink, canary.Name);
                }
            }

            return cases;
        }
    }

    public static TheoryData<string> Sinks => new(CanaryScan.Sinks);

    /// <summary>
    /// The marker shares an event with the canary, so finding it proves the event arrived. Without
    /// that, an event the gate dropped would look the same as one it masked.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_secret_reaches_no_sink(string sink, string name)
    {
        var canary = CanaryCases.All.Single(candidate => candidate.Name == name);
        var output = scan.Output(sink);

        Assert.Contains(CanaryScan.Marker(name), output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary.Canary, output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void A_secret_in_the_configuration_is_masked_with_no_marker_to_find_it_by(string sink)
    {
        var output = scan.Output(sink);

        Assert.Contains("markerKnownProperty", output, StringComparison.Ordinal);
        Assert.Contains("markerKnownPassword", output, StringComparison.Ordinal);
        Assert.Contains("markerKnownException", output, StringComparison.Ordinal);
        Assert.DoesNotContain(scan.ConfiguredSecret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(scan.ConnectionPassword, output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void Masking_leaves_ordinary_content_alone(string sink)
    {
        var output = scan.Output(sink);

        Assert.Contains("1234", output, StringComparison.Ordinal);
        Assert.Contains("5678", output, StringComparison.Ordinal);
        Assert.Contains("6912", output, StringComparison.Ordinal);
        Assert.Contains("tokens: 4321", output, StringComparison.Ordinal);
        Assert.Contains("providers", output, StringComparison.Ordinal);
        Assert.Contains("already exists", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host loads environment variables into the configuration, and PWD would mask every path in
    /// every stack trace. An override key can contain a sensitive word, but its value is a level name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sinks))]
    public void Settings_that_only_look_sensitive_arent_treated_as_secrets(string sink)
    {
        var output = scan.Output(sink);

        Assert.Contains($"{CanaryScan.WorkingDirectory}/Ecnanib.dll", output, StringComparison.Ordinal);
        Assert.Matches(@"markerLevelName\s+\\?""?Information", output);
    }
}
