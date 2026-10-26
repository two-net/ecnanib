using Ecnanib.Accounts.Configuration;
using Ecnanib.Agents.Configuration;
using Ecnanib.Ai.Configuration;
using Ecnanib.Configuration;
using Ecnanib.Council.Configuration;
using Ecnanib.Database.Configuration;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Execution.Configuration;
using Ecnanib.Experience.Configuration;
using Ecnanib.Keys.Configuration;
using Ecnanib.Logging.Configuration;
using Ecnanib.MarketData.Configuration;
using Ecnanib.Tests.Support;
using Ecnanib.Ui.Configuration;
using Microsoft.Extensions.Configuration;

namespace Ecnanib.Tests.Configuration;

/// <summary>What the JSON files may contain (FR-CFG-06, FR-CFG-08) and how a file that cannot be loaded is described.</summary>
public class ConfigurationFileTests
{
    [Fact]
    public void UnknownTopLevelSectionsAreRejectedAndTheKnownOnesListed()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("appsettings.json", "{ \"Server\": {}, \"Databse\": { \"ConnectionString\": \"x\" }, \"ConnectionStrings\": { \"Default\": \"x\" } }");

        var ex = Assert.Throws<ConfigurationException>(() => Verify(path));

        Assert.Contains($"Configuration file {path} has unknown top-level section(s): 'ConnectionStrings', 'Databse'.", ex.Message);
        Assert.Contains("Known sections: Accounts, Agents, AllowedHosts, Council, Database, Exchange, Execution, Experience, Kestrel, Keys, Logging, MarketData, Paper, Providers, Retention, RiskCaps, Server, Ui.", ex.Message);
    }

    [Fact]
    public void TheFrameworkSectionsTheSchemaKeyAndCaseDifferencesAreAccepted()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("appsettings.json", "{ \"$schema\": \"x\", \"server\": { \"PublicUrl\": \"http://localhost:1\" }, \"DATABASE\": { \"ConnectionString\": \"\" }, \"Kestrel\": { \"Endpoints\": { \"Http\": { \"Url\": \"http://127.0.0.1:1\" } } }, \"AllowedHosts\": \"*\" }");

        var configuration = Verify(path);

        Assert.Equal("http://localhost:1", configuration["Server:PublicUrl"]);
        Assert.Equal("http://127.0.0.1:1", configuration["Kestrel:Endpoints:Http:Url"]);
    }

    [Fact]
    public void TheOverlayIsCheckedTooAndNamedWhenItIsTheCulprit()
    {
        using var dir = new TempDirectory();
        var reference = dir.Write("appsettings.json", "{ \"Server\": {} }");
        var overlay = dir.Write("appsettings.Development.json", "{ \"Database\": {}, \"Nope\": 1 }");

        var ex = Assert.Throws<ConfigurationException>(() => Verify(reference, overlay));

        Assert.Contains($"Configuration file {overlay} has unknown top-level section(s): 'Nope'.", ex.Message);
    }

    /// <summary>Environment variables and the command line carry the host's own keys and whatever else the machine has; only the files are checked.</summary>
    [Fact]
    public void OnlyTheJsonFilesAreChecked()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Production", ["PATH"] = "/usr/bin" });
        configuration.AddCommandLine(["--contentRoot", "/somewhere", "--urls", "http://127.0.0.1:1"]);

        ConfigurationFile.VerifySections(configuration);

        Assert.Equal("/somewhere", configuration["contentRoot"]);
    }

    [Fact]
    public void MalformedJsonIsDescribedWithItsLineAndColumn()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("appsettings.json", "{\n  \"Server\": {\n    \"Port\": 5207,,\n  }\n}\n");

        var ex = Assert.Throws<InvalidDataException>(() => Verify(path));
        var message = ConfigurationFile.DescribeLoadFailure(ex);

        Assert.Contains(path, message);
        Assert.Contains("It is not valid JSON at line 3, column 18:", message);
        Assert.DoesNotContain("LineNumber:", message);
    }

    [Theory]
    [InlineData("{ \"Server\": { \"PublicUrl\": \"a\" }, \"Server\": { \"PublicUrl\": \"b\" } }", "A duplicate key 'Server:PublicUrl' was found.")]
    [InlineData("[]", "must be an object")]
    public void OtherLoadFailuresKeepTheParsersReason(string content, string expected)
    {
        using var dir = new TempDirectory();
        var path = dir.Write("appsettings.json", content);

        var ex = Assert.Throws<InvalidDataException>(() => Verify(path));
        var message = ConfigurationFile.DescribeLoadFailure(ex);

        Assert.Contains(path, message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void SectionsMatchTheModuleConstantsAndTheFrameworks()
    {
        string[] expected =
        [
            ServerOptions.SectionName, DatabaseOptions.SectionName, LoggingOptions.SectionName, ExchangeOptions.SectionName,
            AiOptions.SectionName, AgentLimitsOptions.SectionName, AgentsOptions.SectionName, RiskCapsOptions.SectionName, PaperOptions.SectionName,
            RetentionOptions.SectionName, AccountsOptions.SectionName, KeysOptions.SectionName, CouncilOptions.SectionName, ExperienceOptions.SectionName,
            MarketDataOptions.SectionName, ExecutionOptions.SectionName, UiOptions.SectionName,
        ];

        // Accounts (fair-use limits) and Agents (runtime settings) bind the same section, so the constant appears twice here and once in the file.
        Assert.Equal(expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), ConfigurationFile.Sections.Order(StringComparer.Ordinal));
        Assert.Equal(LoggingOptions.SectionName, ConfigurationFile.LoggingSection);
        Assert.Equal(["Kestrel", "AllowedHosts"], ConfigurationFile.FrameworkSections);
    }

    [Fact]
    public void TheFileNamesAreTheFrameworks()
    {
        Assert.Equal("appsettings.json", ConfigurationFile.FileName);
        Assert.Equal("appsettings.Development.json", ConfigurationFile.EnvironmentFileName("Development"));
    }

    private static ConfigurationManager Verify(params string[] paths)
    {
        var configuration = new ConfigurationManager();
        foreach (var path in paths)
            configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        ConfigurationFile.VerifySections(configuration);
        return configuration;
    }
}
