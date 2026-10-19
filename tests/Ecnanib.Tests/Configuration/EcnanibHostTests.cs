using Ecnanib.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Ecnanib.Tests.Configuration;

public class EcnanibHostTests
{
    [Fact]
    public void TheBuilderReadsTheFileAndNothingElse()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("ecnanib.json", "{ \"Server\": { \"Port\": 6001 } }");

        var builder = EcnanibHost.CreateBuilder(path);

        var json = Assert.Single(builder.Configuration.Sources.OfType<JsonConfigurationSource>());
        Assert.False(json.Optional);
        Assert.False(json.ReloadOnChange);
        Assert.DoesNotContain(builder.Configuration.Sources, s => s is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource);
        Assert.Equal("6001", builder.Configuration["Server:Port"]);
    }

    [Fact]
    public void ThereIsNoDevelopmentEnvironmentAndTheContentRootIsTheCurrentDirectory()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("ecnanib.json", "{ }");

        var builder = EcnanibHost.CreateBuilder(path);

        Assert.True(builder.Environment.IsProduction());
        Assert.Equal(Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory()), Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath));
    }

    [Fact]
    public void TheBuilderAddsKestrelRoutingAndTheConsoleLogger()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("ecnanib.json", "{ \"Logging\": { \"LogLevel\": { \"Default\": \"Warning\" } } }");

        var builder = EcnanibHost.CreateBuilder(path);

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IServer));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(ConsoleLoggerProvider));
        Assert.Contains(builder.Services, d => d.ServiceType.Name == "LinkGenerator");
    }

    [Fact]
    public void AnInvalidFileFailsBeforeAnythingIsBuilt()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("ecnanib.json", "{ \"Nope\": 1 }");

        var ex = Assert.Throws<ConfigurationException>(() => EcnanibHost.CreateBuilder(path));

        Assert.Contains("'Nope'", ex.Message);
    }
}
