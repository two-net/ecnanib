using System.Reflection;

namespace Ecnanib.Platform.Settings;

/// <summary>
/// Every application setting, read once at startup from the one configuration file (NFR-CFG-1).
/// A component takes the section it needs as a constructor parameter. Nothing here changes while
/// the application runs: user preferences, agent settings, and runtime flags live in the database
/// (NFR-CFG-6).
/// </summary>
public sealed record EcnanibSettings(
    ApplicationSettings Application,
    ConnectionStringsSettings ConnectionStrings,
    SecuritySettings Security,
    LoggingSettings Logging,
    EmailSettings Email,
    AiProvidersSettings AiProviders,
    CatalogSyncSettings CatalogSync,
    BinanceSettings Binance,
    TradingSettings Trading,
    ExperienceSettings Experience,
    RetentionSettings Retention,
    LimitsSettings Limits)
{
    /// <summary>The names of the file's top-level sections, which are the properties above. They ignore case.</summary>
    internal static IReadOnlySet<string> Sections { get; } = typeof(EcnanibSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(section => section.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static EcnanibSettings Read(SettingsReader file)
    {
        var application = ApplicationSettings.Read(file);
        var connectionStrings = ConnectionStringsSettings.Read(file);
        var security = SecuritySettings.Read(file);
        var logging = LoggingSettings.Read(file);
        var email = EmailSettings.Read(file);
        var aiProviders = AiProvidersSettings.Read(file);
        var catalogSync = CatalogSyncSettings.Read(file);
        var binance = BinanceSettings.Read(file);
        var trading = TradingSettings.Read(file, aiProviders);
        var experience = ExperienceSettings.Read(file);
        var retention = RetentionSettings.Read(file);
        var limits = LimitsSettings.Read(file);

        return new EcnanibSettings(
            application, connectionStrings, security, logging, email, aiProviders, catalogSync, binance, trading,
            experience, retention, limits);
    }
}
