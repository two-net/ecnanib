using Ecnanib.Platform;
using Ecnanib.Platform.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// The configuration file is the only source of application settings. A value supplied any other
/// way has no effect (NFR-CFG-2), while the variables the runtime platform defines for itself keep
/// their default behavior (NFR-CFG-3).
/// </summary>
public sealed class SingleSourceTests : IDisposable
{
    private const string Key = "Limits:MaxAgentsPerUser";
    private const int FromTheFile = 7;
    private const string FromElsewhere = "999";

    private readonly SettingsDirectory _contentRoot = new(SettingsJson.Valid().OnTestServer().With(Key, FromTheFile));

    public void Dispose() => _contentRoot.Dispose();

    [Fact]
    public void An_environment_variable_has_no_effect()
    {
        using var variable = new EnvironmentVariable("Limits__MaxAgentsPerUser", FromElsewhere);
        using var application = new EcnanibApplicationFactory(_contentRoot);

        AssertOnlyTheFileCounts(application);
    }

    [Theory]
    [InlineData("ASPNETCORE_Limits__MaxAgentsPerUser")]
    [InlineData("DOTNET_LIMITS__MAXAGENTSPERUSER")]
    public void An_environment_variable_with_the_platforms_prefix_has_no_effect_either(string name)
    {
        using var variable = new EnvironmentVariable(name, FromElsewhere);
        using var application = new EcnanibApplicationFactory(_contentRoot);

        AssertOnlyTheFileCounts(application);
    }

    [Fact]
    public void A_platform_variable_cannot_set_log_levels()
    {
        // The built-in logger reads the host's configuration, so the Logging section must not reach it.
        using var variable = new EnvironmentVariable("ASPNETCORE_Logging__LogLevel__Default", "Trace");
        using var application = new EcnanibApplicationFactory(_contentRoot);

        var logger = application.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecnanib.Anything");

        Assert.Empty(application.Services.GetRequiredService<IConfiguration>().GetSection("Logging").GetChildren());
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void A_command_line_argument_has_no_effect()
    {
        // The factory hands host settings to the application as command-line arguments.
        using var application = new EcnanibApplicationFactory(_contentRoot, host => host.UseSetting(Key, FromElsewhere));

        AssertOnlyTheFileCounts(application);
    }

    [Theory]
    [InlineData("appsettings.Development.json")]
    [InlineData("Ecnanib.settings.json")]
    [InlineData("Ecnanib.settings.Development.json")]
    public void A_per_environment_or_other_override_file_has_no_effect(string name)
    {
        // The factory runs the application in the Development environment.
        _contentRoot.Write(name, $$"""{ "Limits": { "MaxAgentsPerUser": {{FromElsewhere}} } }""");
        using var application = new EcnanibApplicationFactory(_contentRoot);

        AssertOnlyTheFileCounts(application);
    }

    [Fact]
    public void Only_the_platforms_own_sources_remain_in_the_hosts_configuration()
    {
        _contentRoot.Write("appsettings.Staging.json", $$"""{ "Limits": { "MaxAgentsPerUser": {{FromElsewhere}} } }""");

        var builder = PlatformModule.CreateBuilder(
            [$"--contentRoot={_contentRoot.Path}", "--environment=Staging", $"--{Key}={FromElsewhere}"]);

        // No appsettings file, user secrets, unprefixed variables, or command line: an external
        // secret store or any other source would have to be added here to have an effect. What
        // is left is one in-memory source that holds the platform's own values.
        Assert.IsType<MemoryConfigurationSource>(Assert.Single(builder.Configuration.Sources));

        // Not even the configuration file itself: settings are typed, and secrets stay out of here.
        Assert.Null(builder.Configuration[Key]);
        Assert.Null(builder.Configuration["Database:ConnectionString"]);

        // The host switches were taken before the sources were removed.
        Assert.Equal("Staging", builder.Environment.EnvironmentName);
        Assert.Equal("Staging", builder.Configuration[HostDefaults.EnvironmentKey]);
        Assert.Equal(_contentRoot.Path, builder.Environment.ContentRootPath.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void The_platforms_own_variables_keep_their_default_behavior()
    {
        using var environment = new EnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Staging");
        using var urls = new EnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5005");
        using var server = new EnvironmentVariable("ASPNETCORE_Kestrel__Limits__MaxRequestBodySize", "1024");

        var builder = PlatformModule.CreateBuilder([$"--contentRoot={_contentRoot.Path}"]);

        Assert.True(builder.Environment.IsStaging());
        Assert.Equal("http://localhost:5005", builder.Configuration[WebHostDefaults.ServerUrlsKey]);

        // A section of the platform's own, even one whose name contains a section of the file.
        Assert.Equal("1024", builder.Configuration["Kestrel:Limits:MaxRequestBodySize"]);
    }

    private static void AssertOnlyTheFileCounts(EcnanibApplicationFactory application)
    {
        Assert.Equal(FromTheFile, application.Services.GetRequiredService<EcnanibSettings>().Limits.MaxAgentsPerUser);
        Assert.Equal(FromTheFile, application.Services.GetRequiredService<LimitsSettings>().MaxAgentsPerUser);

        // The value did not reach the host's configuration either.
        Assert.Null(application.Services.GetRequiredService<IConfiguration>()[Key]);
    }

    /// <summary>Sets an environment variable for the length of a test.</summary>
    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _before;

        public EnvironmentVariable(string name, string value)
        {
            _name = name;
            _before = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _before);
    }
}
