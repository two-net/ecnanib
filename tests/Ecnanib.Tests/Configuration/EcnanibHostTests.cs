using Ecnanib.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Configuration;

/// <summary>
/// The host is the framework's default one (FR-CFG-01, FR-CFG-02): the standard sources in their standard order over a
/// content root and an environment the tests name on the command line, so no test depends on the working directory or
/// on what the test runner's environment says.
/// </summary>
public class EcnanibHostTests
{
    [Fact]
    public void TheStandardSourcesAreReadInTheStandardOrder()
    {
        using var dir = new TempDirectory();
        dir.Write("appsettings.json", "{ \"Server\": { \"PublicUrl\": \"http://localhost:6001\" }, \"Database\": { \"CommandTimeoutSeconds\": 30 } }");
        dir.Write("appsettings.Development.json", "{ \"Database\": { \"CommandTimeoutSeconds\": 31 } }");

        var builder = Create(dir, "Development");

        // .NET 10 also reads {ApplicationName}.settings.json and its overlay, after appsettings.json and its overlay.
        var name = builder.Environment.ApplicationName;
        var json = builder.Configuration.Sources.OfType<JsonConfigurationSource>().ToList();
        Assert.Equal(new[] { "appsettings.json", "appsettings.Development.json", $"{name}.settings.json", $"{name}.settings.Development.json" }, json.Select(s => s.Path!).ToArray());
        Assert.All(json, s =>
        {
            Assert.True(s.Optional);
            Assert.True(s.ReloadOnChange);
        });
        Assert.Contains(builder.Configuration.Sources, s => s is EnvironmentVariablesConfigurationSource);
        Assert.Contains(builder.Configuration.Sources, s => s is CommandLineConfigurationSource);
        Assert.Equal("http://localhost:6001", builder.Configuration["Server:PublicUrl"]);
        Assert.Equal("31", builder.Configuration["Database:CommandTimeoutSeconds"]);
        Assert.True(builder.Environment.IsDevelopment());
    }

    [Fact]
    public void TheEnvironmentAndTheCommandLineOverrideTheFiles()
    {
        using var dir = new TempDirectory();
        dir.Write("appsettings.json", "{ \"Database\": { \"CommandTimeoutSeconds\": 30, \"RetryOnFailureCount\": 3 } }");
        // A value in range, in case the variable leaks into a child process another test starts meanwhile.
        Environment.SetEnvironmentVariable("Database__CommandTimeoutSeconds", "45");
        try
        {
            var builder = Create(dir, "Production", "--Database:RetryOnFailureCount=7");

            Assert.Equal("45", builder.Configuration["Database:CommandTimeoutSeconds"]);
            Assert.Equal("7", builder.Configuration["Database:RetryOnFailureCount"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Database__CommandTimeoutSeconds", null);
        }
    }

    [Fact]
    public void MissingFilesAreAllowedAndTheContentRootIsTheOneNamed()
    {
        using var dir = new TempDirectory();

        var builder = Create(dir, "Production");

        Assert.True(builder.Environment.IsProduction());
        Assert.Equal(Path.TrimEndingDirectorySeparator(dir.Path), Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath));
        Assert.Null(builder.Configuration["Server:PublicUrl"]);
    }

    [Fact]
    public void TheBuilderHasKestrelFromItsSectionAndRouting()
    {
        using var dir = new TempDirectory();

        var builder = Create(dir, "Production");

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IServer));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IConfigureOptions<KestrelServerOptions>));
        Assert.Contains(builder.Services, d => d.ServiceType.Name == "LinkGenerator");
    }

    [Fact]
    public void AnUnknownSectionFailsBeforeAnythingIsBuilt()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("appsettings.json", "{ \"Nope\": 1 }");

        var ex = Assert.Throws<ConfigurationException>(() => Create(dir, "Production"));

        Assert.Contains("'Nope'", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void AMalformedFileFailsBeforeAnythingIsBuiltWithItsLineAndColumn()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("appsettings.Development.json", "{\n  \"Server\": {\n    \"Port\": 5207,,\n  }\n}\n");

        var ex = Assert.Throws<ConfigurationException>(() => Create(dir, "Development"));

        Assert.Contains(path, ex.Message);
        Assert.Contains("It is not valid JSON at line 3, column 18:", ex.Message);
        Assert.IsType<InvalidDataException>(ex.InnerException);
    }

    private static WebApplicationBuilder Create(TempDirectory dir, string environment, params string[] more) =>
        EcnanibHost.CreateBuilder(["--contentRoot", dir.Path, "--environment", environment, .. more]);
}
