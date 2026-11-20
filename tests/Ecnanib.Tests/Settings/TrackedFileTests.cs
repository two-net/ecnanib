using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
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

        // Binance's own addresses, of each market on the real exchange and in its test
        // environment, and of no other exchange (FR-BIN-1, FR-BIN-2).
        Assert.Equal(new Uri("https://api.binance.com"), settings.Binance.BaseUrl(Market.Spot, TradingMode.Live));
        Assert.Equal(new Uri("https://demo-api.binance.com"), settings.Binance.BaseUrl(Market.Spot, TradingMode.Demo));
        Assert.Equal(new Uri("https://fapi.binance.com"), settings.Binance.BaseUrl(Market.Futures, TradingMode.Live));
        Assert.Equal(new Uri("https://demo-fapi.binance.com"), settings.Binance.BaseUrl(Market.Futures, TradingMode.Demo));

        // Binance's own addresses of its account event feeds, each reached over TLS (FR-BIN-7):
        // its WebSocket API on the Spot market, and the address that its user data streams are
        // under on the Futures market.
        Assert.Equal(new Uri("wss://ws-api.binance.com:443/ws-api/v3"), settings.Binance.FeedUrl(Market.Spot, TradingMode.Live));
        Assert.Equal(new Uri("wss://demo-ws-api.binance.com:443/ws-api/v3"), settings.Binance.FeedUrl(Market.Spot, TradingMode.Demo));
        Assert.Equal(new Uri("wss://fstream.binance.com/private"), settings.Binance.FeedUrl(Market.Futures, TradingMode.Live));
        Assert.Equal(new Uri("wss://demo-fstream.binance.com/private"), settings.Binance.FeedUrl(Market.Futures, TradingMode.Demo));
        Assert.Equal(70, settings.Binance.LimitSharePercent);
        Assert.Equal(5, settings.Binance.ReconcileMinutes);

        // The clocks of Binance are read every minute, and the clock of the service may be half
        // a second from them, which is half of what Binance takes a signed call with (FR-BIN-5).
        Assert.Equal(60, settings.Binance.ClockCheckSeconds);
        Assert.Equal(500, settings.Binance.ClockToleranceMilliseconds);
        Assert.Equal(
            new AggressiveSettings(
                RiskPerTradePercent: new Bounded<decimal>(0.5m, 2m, 5m),
                MinRewardToRisk: new Floored<decimal>(1.0m, 1.5m),
                LeverageCap: new Bounded<int>(1, 5, 10),
                LiquidationBuffer: new Floored<decimal>(1.5m, 2.0m),
                DailyLossPercent: new Bounded<decimal>(2m, 6m, 15m),
                DrawdownPercent: new Bounded<decimal>(10m, 25m, 50m),
                MaxEntryDistancePercent: new Bounded<decimal>(0.1m, 1m, 3m)),
            settings.Trading.Aggressive);
        Assert.Equal(new StaleSecondsSettings(120, 300, 600, 900), settings.Trading.StaleSeconds);

        // The cycles of 500 agents that share a boundary start in two rounds at most, and a
        // user's key is called for four of their agents at once (FR-CYC-11, NFR-PRF-2).
        Assert.Equal(new CycleSettings(MaxPerProvider: 250, MaxPerKey: 4), settings.Trading.Cycles);

        // The data package holds the most recent 200 closed candles of the agent's timeframe
        // (FR-CYC-2), and 50 of each higher timeframe for context.
        Assert.Equal(new DataPackageSettings(Candles: 200, ContextCandles: 50), settings.Trading.DataPackage);

        // The user of a position of the Futures market is told that it approaches liquidation
        // when a fifth of the way from its entry to its liquidation price is left (FR-BIN-F5).
        // A stop-loss that the least liquidation buffer above allows is no further than about
        // seven tenths of that way, so one that works closes the position first.
        Assert.Equal(20, settings.Trading.LiquidationWarningPercent);

        // The roles of an agent whose user turned it on are reassigned by strength at most once
        // every 7 days (FR-TEAM-10).
        Assert.Equal(7, settings.Trading.RoleReassignmentDays);

        // A shadow trade is charged what Binance charges an account with no discount for an
        // order that takes: a tenth of a percent on Spot, and a twentieth on Futures (FR-EXP-5).
        Assert.Equal(
            new ExperienceSettings(
                5, 20, 20, new ShadowFeeSettings(Spot: 0.1m, Futures: 0.05m), new ExperienceBoardSettings(MinUsers: 5, MinTrades: 50)),
            settings.Experience);
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
