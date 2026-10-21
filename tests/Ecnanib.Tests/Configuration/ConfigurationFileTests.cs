using Ecnanib.Accounts.Configuration;
using Ecnanib.Agents.Configuration;
using Ecnanib.Ai.Configuration;
using Ecnanib.Configuration;
using Ecnanib.Council.Configuration;
using Ecnanib.Database.Configuration;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Experience.Configuration;
using Ecnanib.Keys.Configuration;
using Ecnanib.MarketData.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace Ecnanib.Tests.Configuration;

public class ConfigurationFileTests
{
    [Fact]
    public void DefaultPathIsEcnanibJsonInTheCurrentDirectory()
    {
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "ecnanib.json"), ConfigurationFile.ResolvePath([]));
    }

    [Theory]
    [InlineData("--config", "conf/app.json")]
    [InlineData("--config=conf/app.json", null)]
    public void ConfigOptionResolvesToAFullPath(string first, string? second)
    {
        var args = second is null ? new[] { first } : new[] { first, second };

        Assert.Equal(Path.GetFullPath("conf/app.json"), ConfigurationFile.ResolvePath(args));
    }

    [Fact]
    public void AbsoluteConfigPathIsKept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere", "ecnanib.json");

        Assert.Equal(absolute, ConfigurationFile.ResolvePath(["--config", absolute]));
    }

    [Theory]
    [InlineData(new[] { "--config" }, "--config requires a path")]
    [InlineData(new[] { "--config=" }, "--config requires a path")]
    [InlineData(new[] { "--config", "a.json", "--config", "b.json" }, "more than once")]
    [InlineData(new[] { "--bogus" }, "Unknown argument '--bogus'")]
    [InlineData(new[] { "ecnanib.json" }, "Unknown argument 'ecnanib.json'")]
    public void BadArgumentsExplainTheUsage(string[] args, string expected)
    {
        var ex = Assert.Throws<ConfigurationException>(() => ConfigurationFile.ResolvePath(args));

        Assert.Contains(expected, ex.Message);
        Assert.Contains("Usage: ecnanib [--config <path>]", ex.Message);
    }

    [Fact]
    public void MissingFileNamesTheFullPath()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "missing.json");

        var ex = Assert.Throws<ConfigurationException>(() => Load(path));

        Assert.Contains("Configuration file not found: " + path, ex.Message);
    }

    [Fact]
    public void MalformedJsonReportsTheLineAndColumn()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("bad.json", "{\n  \"Server\": {\n    \"Port\": 5207,,\n  }\n}\n");

        var ex = Assert.Throws<ConfigurationException>(() => Load(path));

        Assert.Contains($"Configuration file {path} is not valid JSON at line 3, column 18:", ex.Message);
        Assert.DoesNotContain("LineNumber:", ex.Message);
    }

    [Fact]
    public void DuplicateSectionsAreRejected()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("dup.json", "{ \"Server\": { \"Port\": 1 }, \"Server\": { \"Port\": 2 } }");

        var ex = Assert.Throws<ConfigurationException>(() => Load(path));

        Assert.Contains($"Configuration file {path} is not valid: A duplicate key 'Server:Port' was found.", ex.Message);
    }

    [Fact]
    public void ArrayRootIsRejected()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("array.json", "[]");

        var ex = Assert.Throws<ConfigurationException>(() => Load(path));

        Assert.Contains("must be an object", ex.Message);
    }

    [Fact]
    public void UnknownTopLevelSectionsAreRejectedAndTheKnownOnesListed()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("typo.json", "{ \"Server\": {}, \"Databse\": { \"ConnectionString\": \"x\" }, \"AllowedHosts\": \"*\" }");

        var ex = Assert.Throws<ConfigurationException>(() => Load(path));

        Assert.Contains($"Configuration file {path} has unknown top-level section(s): 'AllowedHosts', 'Databse'.", ex.Message);
        Assert.Contains("Known sections: Accounts, Agents, Council, Database, Exchange, Experience, Keys, Logging, MarketData, Paper, Providers, Retention, RiskCaps, Server.", ex.Message);
    }

    [Fact]
    public void SchemaKeyAndCaseDifferencesAreAccepted()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("case.json", "{ \"$schema\": \"x\", \"server\": { \"Port\": 1 }, \"DATABASE\": { \"ConnectionString\": \"\" } }");

        var configuration = Load(path);

        Assert.Equal("1", configuration["Server:Port"]);
    }

    [Fact]
    public void TheFileIsTheOnlySourceAndIsNotWatched()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("ecnanib.json", "{ \"Server\": { \"Port\": 6001 } }");

        var configuration = Load(path);

        // A ConfigurationManager starts with an empty in-memory source of its own; the file is the only one added.
        var source = Assert.Single(configuration.Sources.OfType<JsonConfigurationSource>());
        Assert.False(source.Optional);
        Assert.False(source.ReloadOnChange);
        Assert.All(configuration.Sources, s => Assert.True(s is JsonConfigurationSource or MemoryConfigurationSource));
        Assert.Single(((IConfigurationRoot)configuration).Providers.OfType<JsonConfigurationProvider>());
    }

    [Fact]
    public void EnvironmentVariablesAreNotRead()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("ecnanib.json", "{ \"Database\": { \"CommandTimeoutSeconds\": 30 } }");
        Environment.SetEnvironmentVariable("Database__CommandTimeoutSeconds", "999");
        Environment.SetEnvironmentVariable("Server__Port", "999");
        try
        {
            var configuration = Load(path);

            Assert.Equal("30", configuration["Database:CommandTimeoutSeconds"]);
            Assert.Null(configuration["Server:Port"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Database__CommandTimeoutSeconds", null);
            Environment.SetEnvironmentVariable("Server__Port", null);
        }
    }

    [Fact]
    public void SectionsMatchTheModuleConstants()
    {
        string[] expected =
        [
            ServerOptions.SectionName, DatabaseOptions.SectionName, ConfigurationFile.LoggingSection, ExchangeOptions.SectionName,
            AiOptions.SectionName, AgentLimitsOptions.SectionName, AgentsOptions.SectionName, RiskCapsOptions.SectionName, PaperOptions.SectionName,
            RetentionOptions.SectionName, AccountsOptions.SectionName, KeysOptions.SectionName, CouncilOptions.SectionName, ExperienceOptions.SectionName,
            MarketDataOptions.SectionName,
        ];

        // Accounts (fair-use limits) and Agents (runtime settings) bind the same section, so the constant appears twice here and once in the file.
        Assert.Equal(expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), ConfigurationFile.Sections.Order(StringComparer.Ordinal));
    }

    private static ConfigurationManager Load(string path)
    {
        var configuration = new ConfigurationManager();
        ConfigurationFile.Load(configuration, path);
        return configuration;
    }
}
