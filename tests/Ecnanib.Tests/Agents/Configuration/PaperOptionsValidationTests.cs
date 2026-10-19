using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Agents.Configuration;

public class PaperOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("Paper", PaperOptions.SectionName);
        Assert.Equal(10_000m, options.StartingBalance);
        Assert.Equal(0.001m, options.MakerFeeRate(MarketType.Spot));
        Assert.Equal(0.001m, options.TakerFeeRate(MarketType.Spot));
        Assert.Equal(0.0002m, options.MakerFeeRate(MarketType.UsdFutures));
        Assert.Equal(0.0005m, options.TakerFeeRate(MarketType.UsdFutures));
        Assert.Equal(0.0005m, options.SlippageRate);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Paper:StartingBalance"] = "500", ["Paper:SlippagePercent"] = "0.2" });

        Assert.Equal(500m, options.StartingBalance);
        Assert.Equal(0.002m, options.SlippageRate);
    }

    [Theory]
    [InlineData("Paper:StartingBalance", "0", "Paper:StartingBalance must be > 0")]
    [InlineData("Paper:SpotMakerFeePercent", "-1", "Paper:SpotMakerFeePercent must be between 0 and 10")]
    [InlineData("Paper:FuturesTakerFeePercent", "11", "Paper:FuturesTakerFeePercent must be between 0 and 10")]
    [InlineData("Paper:SlippagePercent", "11", "Paper:SlippagePercent must be between 0 and 10")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static PaperOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = AgentsTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<PaperOptions>>().Value;
    }
}
