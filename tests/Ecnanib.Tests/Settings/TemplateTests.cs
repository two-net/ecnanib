using Ecnanib.Platform.Settings;

namespace Ecnanib.Tests.Settings;

/// <summary>
/// The template in source control has every key the application reads and nothing else, with
/// placeholders where a deployment must supply its own value (NFR-CFG-5).
/// </summary>
public class TemplateTests
{
    // The template ships next to the application, so it is in the test output as well.
    private static readonly string Template =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, SettingsFile.TemplateName));

    [Fact]
    public void The_template_is_wrong_only_where_it_holds_a_placeholder()
    {
        using var directory = new SettingsDirectory(Template);

        var invalid = Assert.Throws<InvalidSettingsException>(() => SettingsFile.Load(directory.SettingsPath));

        // Anything else here means the template and the settings model have drifted apart: a
        // missing key, an unknown key, or a default that is out of range.
        Assert.Equal(
            [
                "Application:PublicUrl",
                "Database:ConnectionString",
                "Security:MasterKeys:key-1",
                "Email:Host",
                "Email:From",
            ],
            invalid.Problems.Select(problem => problem.Key));
        Assert.All(invalid.Problems, problem => Assert.Equal("still holds the placeholder from the template", problem.Rule));
    }

    [Fact]
    public void The_template_is_valid_once_its_placeholders_are_replaced_and_carries_the_specified_defaults()
    {
        using var directory = new SettingsDirectory(SettingsJson.Parse(Template)
            .With("Application:PublicUrl", "https://ecnanib.example")
            .With("Database:ConnectionString", "Host=db.example;Database=ecnanib")
            .With("Security:MasterKeys:key-1", SettingsJson.MasterKey)
            .With("Email:Host", "mail.example")
            .With("Email:From", "noreply@ecnanib.example"));

        var settings = SettingsFile.Load(directory.SettingsPath);

        // SRS section 7 and Appendix B.
        Assert.Equal("Ecnanib", settings.Application.Name);
        Assert.True(settings.Application.RegistrationOpen);
        Assert.Equal(60, settings.Security.SessionIdleMinutes);
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
        Assert.Equal(new LimitsSettings(MaxAgentsPerUser: 10), settings.Limits);
    }
}
