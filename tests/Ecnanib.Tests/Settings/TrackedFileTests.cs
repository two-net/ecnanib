using Ecnanib.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// The configuration file in source control, <c>src/Ecnanib/appsettings.json</c>, has every key
/// the application reads and nothing else, and carries the defaults from the specification.
/// </summary>
public class TrackedFileTests
{
    // The test project copies the tracked file into its output under this name.
    private static readonly string Tracked =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.tracked.json"));

    [Fact]
    public void The_tracked_file_is_valid_and_carries_the_specified_defaults()
    {
        using var directory = new SettingsDirectory(Tracked);

        // A problem here means the file and the settings model have drifted apart: a missing key,
        // an unknown key, or a default that is out of range.
        var settings = SettingsFile.Load(directory.SettingsPath);

        // SRS section 7 and Appendix B.
        Assert.Equal("Ecnanib", settings.Application.Name);
        Assert.True(settings.Application.RegistrationOpen);
        Assert.Equal(60, settings.Security.SessionIdleMinutes);
        Assert.Equal(LogLevel.Information, settings.Logging.MinimumLevel);
        Assert.True(settings.Logging.Outputs.Console);
        Assert.Equal(LogFileRolling.Day, settings.Logging.Outputs.File?.Rolling);
        Assert.Equal(30, settings.Logging.Outputs.File?.RetainedFiles);
        Assert.Equal(4, settings.AiProviders.ByCode.Count);
        Assert.All(settings.AiProviders.ByCode.Values, provider => Assert.Equal(60, provider.TimeoutSeconds));
        Assert.Equal(new CatalogSyncSettings(IntervalHours: 24, MissedSyncsBeforeUnavailable: 2), settings.CatalogSync);
        Assert.Equal(70, settings.Binance.LimitSharePercent);
        Assert.Equal(5, settings.Binance.ReconcileMinutes);
        Assert.Equal(
            new AggressiveSettings(
                RiskPerTradePercent: new Bounded<decimal>(0.5m, 2m, 5m),
                MinRewardToRisk: 1.5m,
                LeverageCap: new Bounded<int>(1, 5, 10),
                LiquidationBuffer: 2.0m,
                DailyLossPercent: new Bounded<decimal>(2m, 6m, 15m),
                DrawdownPercent: new Bounded<decimal>(10m, 25m, 50m),
                MaxEntryDistancePercent: new Bounded<decimal>(0.1m, 1m, 3m)),
            settings.Trading.Aggressive);
        Assert.Equal(new StaleSecondsSettings(120, 300, 600, 900), settings.Trading.StaleSeconds);
        Assert.Equal(new ExperienceSettings(5, 20, 20, new ExperienceBoardSettings(MinUsers: 5, MinTrades: 50)), settings.Experience);
        Assert.Equal(new RetentionSettings(90, 365, 90, 730), settings.Retention);
        Assert.Equal(
            new LimitsSettings(
                MaxAgentsPerUser: 10,
                SignIn: new AttemptLimitSettings(PerClient: 30, PerAddress: 10, Minutes: 15),
                Registration: new AttemptLimitSettings(PerClient: 10, PerAddress: 3, Minutes: 60),
                PasswordReset: new AttemptLimitSettings(PerClient: 10, PerAddress: 3, Minutes: 60)),
            settings.Limits);
    }
}
