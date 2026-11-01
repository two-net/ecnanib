using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.Configuration;

namespace Ecnanib.Tests.Logging;

/// <summary>CFG-03 for the Logging section.</summary>
public sealed class LoggingOptionsTests(PostgresFixture postgres)
{
    private const string FilePath = "Logging:Sinks:File:Path";
    private const string FileEnabled = "Logging:Sinks:File:Enabled";
    private const string Retained = "Logging:Sinks:File:RetainedFileCount";

    public static TheoryData<string, Dictionary<string, string?>> InvalidSettings => new()
    {
        { "Logging:MinimumLevel", new() { ["Logging:MinimumLevel"] = "Loud" } },
        { "Logging:MinimumLevel", new() { ["Logging:MinimumLevel"] = "99" } },
        { "Logging:MinimumLevel", new() { ["Logging:MinimumLevel"] = null } },
        { "Logging:Overrides:Http", new() { ["Logging:Overrides:Http"] = "Loud" } },
        { "Logging:Sinks:Database:MinimumLevel", new() { ["Logging:Sinks:Database:MinimumLevel"] = "Loud" } },
        { "MinimumLevl", new() { ["Logging:MinimumLevl"] = "Debug" } },
        { "LogLevel", new() { ["Logging:LogLevel:Default"] = "Debug" } },
        { FilePath, new() { [FileEnabled] = "true", [Retained] = "14" } },
        { Retained, new() { [FileEnabled] = "true", [FilePath] = "logs/a-.log" } },
        { Retained, new() { [FileEnabled] = "true", [FilePath] = "logs/a-.log", [Retained] = "0" } },
        {
            "Logging:Sinks:File:RollingInterval",
            new() { [FileEnabled] = "true", [FilePath] = "logs/a-.log", [Retained] = "14", ["Logging:Sinks:File:RollingInterval"] = "Hour" }
        },
        {
            "Logging:Sinks:File:FileSizeLimitBytes",
            new() { [FileEnabled] = "true", [FilePath] = "logs/a-.log", [Retained] = "14", ["Logging:Sinks:File:FileSizeLimitBytes"] = "0" }
        },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task An_invalid_setting_stops_startup_and_is_named(string named, Dictionary<string, string?> settings)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: settings);

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(named, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The file ships with the product, and nothing else would notice a mistake in it.</summary>
    [Fact]
    public async Task The_shipped_settings_are_valid()
    {
        var logs = Directory.CreateTempSubdirectory("ecnanib-logs-");

        try
        {
            await using var app = new AppFactory(
                connectionString: null,
                configure: configuration => configuration
                    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"] = postgres.NewConnectionString(),
                        [FilePath] = Path.Combine(logs.FullName, "ecnanib-.log"),
                    }));

            app.Start();
        }
        finally
        {
            logs.Delete(recursive: true);
        }
    }
}
