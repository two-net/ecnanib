using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static Ecnanib.Tests.Risk.RiskHarness;

namespace Ecnanib.Tests.Risk;

/// <summary>FR-RISK-11: no agent setting may exceed the global hard caps in configuration.</summary>
public sealed class RiskCapsTests
{
    [Fact]
    public void The_defaults_are_SRS_7_3s()
    {
        var caps = Provider().GetRequiredService<RiskCaps>();

        Assert.Equal(RiskCaps.Default, caps);
        Assert.Equal(5, caps.MaxLeverage);
        Assert.Equal(2m, caps.MaxRiskPerTradePercent);
        Assert.Equal(2m, caps.MinLiquidationBufferMultiple);
        Assert.Equal(1m, caps.MinRewardToRisk);
    }

    [Fact]
    public void The_SRS_default_settings_are_within_the_default_caps()
    {
        Assert.Empty(RiskCaps.Default.Problems(RiskSettings.Default));
        Assert.Equal(RiskSettings.Default, RiskCaps.Default.Apply(RiskSettings.Default));
    }

    [Fact]
    public void Each_setting_beyond_its_cap_is_named_and_clamped()
    {
        var settings = RiskSettings.Default with
        {
            MaxRiskPerTradePercent = 2.5m,
            MaxLeverage = 6,
            MinRewardToRisk = 0.9m,
            LiquidationBufferMultiple = 1.9m,
        };

        Assert.Equal(
            [RiskSetting.MaxRiskPerTradePercent, RiskSetting.MaxLeverage, RiskSetting.MinRewardToRisk, RiskSetting.LiquidationBufferMultiple],
            RiskCaps.Default.Problems(settings));

        var clamped = RiskCaps.Default.Apply(settings);

        Assert.Equal(2m, clamped.MaxRiskPerTradePercent);
        Assert.Equal(5, clamped.MaxLeverage);
        Assert.Equal(1m, clamped.MinRewardToRisk);
        Assert.Equal(2m, clamped.LiquidationBufferMultiple);
        Assert.Empty(RiskCaps.Default.Problems(clamped));
    }

    [Fact]
    public void A_setting_exactly_at_its_cap_is_allowed()
    {
        var settings = RiskSettings.Default with
        {
            MaxRiskPerTradePercent = 2m,
            MaxLeverage = 5,
            MinRewardToRisk = 1m,
            LiquidationBufferMultiple = 2m,
        };

        Assert.Empty(RiskCaps.Default.Problems(settings));
    }

    [Fact]
    public void Configured_caps_reach_the_engine()
    {
        var engine = Engine(new Dictionary<string, string?> { ["RiskCaps:MaxLeverage"] = "1", ["RiskCaps:MaxRiskPerTradePercent"] = "0.5" });

        var evaluation = engine.Evaluate(FuturesLong() with { TargetPrice = null }, Futures());

        Assert.Equal(1, engine.Caps.MaxLeverage);
        Assert.Equal(1, evaluation.Passes().Leverage);

        // Half a percent of 1,000 is 5, over 20 a unit.
        Assert.Equal(0.25m, evaluation.Order!.Quantity);
    }

    [Theory]
    [InlineData("RiskCaps:MaxLeverage", "0")]
    [InlineData("RiskCaps:MaxLeverage", "126")]
    [InlineData("RiskCaps:MaxLeverage", "2.5")]
    [InlineData("RiskCaps:MaxRiskPerTradePercent", "0")]
    [InlineData("RiskCaps:MaxRiskPerTradePercent", "101")]
    [InlineData("RiskCaps:MaxRiskPerTradePercent", "two")]
    [InlineData("RiskCaps:MinLiquidationBufferMultiple", "0.9")]
    [InlineData("RiskCaps:MinLiquidationBufferMultiple", "1,5")]
    [InlineData("RiskCaps:MinRewardToRisk", "0")]
    [InlineData("RiskCaps:MinRewardToRisk", "-1")]
    public void An_invalid_cap_is_refused_and_named(string key, string value)
    {
        using var provider = Provider(new Dictionary<string, string?> { [key] = value });

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<RiskCaps>());

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Caps_read_in_the_invariant_culture()
    {
        using var provider = Provider(new Dictionary<string, string?> { ["RiskCaps:MinRewardToRisk"] = "1.25" });

        Assert.Equal(1.25m, provider.GetRequiredService<RiskCaps>().MinRewardToRisk);
    }
}

/// <summary>CFG-03: an invalid cap stops startup with a message that names it.</summary>
public sealed class RiskCapsStartupTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_invalid_cap_stops_startup_and_is_named()
    {
        const string key = "RiskCaps:MaxLeverage";
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [key] = "500" });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shipped_caps_start()
    {
        await using var app = new AppFactory(
            postgres.NewConnectionString(),
            settings: new Dictionary<string, string?>
            {
                ["RiskCaps:MaxLeverage"] = "5",
                ["RiskCaps:MaxRiskPerTradePercent"] = "2",
                ["RiskCaps:MinLiquidationBufferMultiple"] = "2",
                ["RiskCaps:MinRewardToRisk"] = "1.0",
            });

        app.Start();
    }
}
