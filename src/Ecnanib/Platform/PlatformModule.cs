using Ecnanib.Platform.Settings;

namespace Ecnanib.Platform;

/// <summary>
/// Platform: the plumbing every component builds on. For now that is configuration; persistence,
/// logging, and background work join it later. See docs/configuration.md.
/// </summary>
public static class PlatformModule
{
    /// <summary>
    /// Creates the host builder with the settings from the one configuration file registered, and
    /// with no other source of settings. The composition root starts here.
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

        ApplyLogLevels(builder.Logging, settings.Logging);

        return builder;
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

    /// <summary>
    /// Applies the minimum level and the per-area overrides to the built-in logger. This is a
    /// stopgap until structured logging takes over the whole Logging section, outputs included.
    /// </summary>
    private static void ApplyLogLevels(ILoggingBuilder logging, LoggingSettings settings)
    {
        logging.SetMinimumLevel(settings.MinimumLevel);

        foreach (var (area, level) in settings.Overrides)
        {
            logging.AddFilter(area, level);
        }
    }
}
