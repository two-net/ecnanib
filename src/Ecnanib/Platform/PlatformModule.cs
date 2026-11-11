using Ecnanib.Platform.Adapters.Http;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Settings;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;

namespace Ecnanib.Platform;

/// <summary>
/// Platform: the plumbing every component builds on. For now that is configuration and logging;
/// persistence and background work join it later. See docs/configuration.md and docs/logging.md.
/// </summary>
public static class PlatformModule
{
    /// <summary>
    /// Creates the host builder with the settings from the one configuration file registered, with
    /// no other source of settings, and with logging as that file describes it. The composition
    /// root starts here.
    /// </summary>
    /// <exception cref="InvalidSettingsException">The file is missing or invalid, so the application must not start.</exception>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        WebApplicationBuilder builder;
        try
        {
            builder = WebApplication.CreateBuilder(args);
        }
        catch (InvalidDataException rejected)
        {
            throw SettingsFile.RejectedByHost(rejected);
        }

        KeepOnlyThePlatformsOwnConfiguration(builder);

        var path = Path.Combine(builder.Environment.ContentRootPath, SettingsFile.Name);
        var settings = SettingsFile.Load(path);

        builder.Services
            .AddSingleton(settings)
            .AddSingleton(settings.Application)
            .AddSingleton(settings.Database)
            .AddSingleton(settings.Security)
            .AddSingleton(settings.Logging)
            .AddSingleton(settings.Email)
            .AddSingleton(settings.AiProviders)
            .AddSingleton(settings.CatalogSync)
            .AddSingleton(settings.Binance)
            .AddSingleton(settings.Trading)
            .AddSingleton(settings.Experience)
            .AddSingleton(settings.Retention)
            .AddSingleton(settings.Limits);

        AddLogging(builder, settings);

        return builder;
    }

    /// <summary>
    /// Replaces the host's default logging with the structured logging the <c>Logging</c> section
    /// describes: its levels, its outputs, and nothing else (NFR-LOG-2). See docs/logging.md.
    /// </summary>
    /// <remarks>
    /// Nothing is opened here. The outputs are created when the host is built, so a builder that
    /// is never built leaves no log file behind.
    /// </remarks>
    private static void AddLogging(WebApplicationBuilder builder, EcnanibSettings settings)
    {
        var contentRoot = builder.Environment.ContentRootPath;
        var mask = SecretMask.For(settings);
        var levels = new LogLevels(settings.Logging);

        // The host's console, debug, and event outputs go: outputs come only from the file.
        builder.Logging.ClearProviders();

        // The trace and span of an event are already part of it, so they are not added as scope properties too.
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.None);

        builder.Services
            .AddSingleton(mask)
            .AddSingleton(levels)
            .AddSingleton<IPostConfigureOptions<LoggerFilterOptions>>(levels)
            .AddSingleton<IOptionsChangeTokenSource<LoggerFilterOptions>>(levels)

            // Registered as a provider of the host's logger factory, which enforces the levels.
            // Serilog's own AddSerilog would add a rule that lets every level through.
            .AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(
                LogPipeline.Create(settings.Logging, contentRoot, mask), dispose: true))
            .AddHostedService<LogLevelReloader>()
            .AddSingleton<UnhandledErrors>()
            .AddExceptionHandler<UnhandledExceptionRecorder>()
            .AddOutboundCallLogging();
    }

    /// <summary>
    /// Removes every configuration source the default host adds for the application: appsettings
    /// files and their per-environment variants, user secrets, unprefixed environment variables,
    /// and command-line arguments (NFR-CFG-2). What stays is what the runtime platform defines for
    /// itself: its <c>ASPNETCORE_</c> and <c>DOTNET_</c> variables, which keep their default
    /// behavior (NFR-CFG-3), and the application name, environment, and content root the host has
    /// already resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sources are cleared and the platform's own values are added back, instead of removing
    /// the unwanted sources by name, because the default list changes between .NET versions.
    /// </para>
    /// <para>
    /// A platform variable cannot carry a section that belongs to the configuration file. Without
    /// this, <c>ASPNETCORE_Logging__LogLevel__Default</c> would set log levels, because the
    /// built-in logger reads the host's configuration.
    /// </para>
    /// <para>
    /// Application settings are never added here. They are read from the file into
    /// <see cref="EcnanibSettings"/>, so nothing in this configuration can override them.
    /// </para>
    /// </remarks>
    private static void KeepOnlyThePlatformsOwnConfiguration(WebApplicationBuilder builder)
    {
        var environment = builder.Environment;

        // The same two sources, in the same order, that the default host reads its own variables from.
        var platformVariables = new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix: "ASPNETCORE_")
            .AddEnvironmentVariables(prefix: "DOTNET_")
            .Build();

        var kept = platformVariables.AsEnumerable()
            .Where(variable => variable.Value is not null && !BelongsToTheConfigurationFile(variable.Key))
            .ToDictionary(StringComparer.OrdinalIgnoreCase);

        kept[HostDefaults.ApplicationKey] = environment.ApplicationName;
        kept[HostDefaults.EnvironmentKey] = environment.EnvironmentName;
        kept[HostDefaults.ContentRootKey] = environment.ContentRootPath;

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(kept);
    }

    private static bool BelongsToTheConfigurationFile(string key)
    {
        var section = key.Split(ConfigurationPath.KeyDelimiter, count: 2)[0];

        return EcnanibSettings.Sections.Contains(section);
    }
}
