using Ecnanib.Agents.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Agents.Configuration;

public class RiskCapsOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("RiskCaps", RiskCapsOptions.SectionName);
        Assert.Equal(2m, options.MaxRiskPerTradePercent);
        Assert.Equal(20, options.MaxLeverage);
        Assert.Equal(10m, options.MaxDailyLossPercent);
        Assert.Equal(5, options.MaxOpenPositions);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["RiskCaps:MaxLeverage"] = "5", ["RiskCaps:MaxRiskPerTradePercent"] = "1.5" });

        Assert.Equal(5, options.MaxLeverage);
        Assert.Equal(1.5m, options.MaxRiskPerTradePercent);
    }

    [Theory]
    [InlineData("RiskCaps:MaxRiskPerTradePercent", "0", "RiskCaps:MaxRiskPerTradePercent must be > 0")]
    [InlineData("RiskCaps:MaxRiskPerTradePercent", "101", "RiskCaps:MaxRiskPerTradePercent must be > 0 and <= 100")]
    [InlineData("RiskCaps:MaxLeverage", "0", "RiskCaps:MaxLeverage must be between 1 and 125")]
    [InlineData("RiskCaps:MaxLeverage", "126", "RiskCaps:MaxLeverage must be between 1 and 125")]
    [InlineData("RiskCaps:MaxDailyLossPercent", "0", "RiskCaps:MaxDailyLossPercent must be > 0")]
    [InlineData("RiskCaps:MaxOpenPositions", "0", "RiskCaps:MaxOpenPositions must be between 1 and 100")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static RiskCapsOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = AgentsTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<RiskCapsOptions>>().Value;
    }
}
