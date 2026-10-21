using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.MarketData.Configuration;

public class MarketDataOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("MarketData", MarketDataOptions.SectionName);
        Assert.Equal(500, options.BackfillFor(CandleInterval.FifteenMinutes));
        Assert.Equal(500, options.BackfillFor(CandleInterval.OneHour));
        Assert.Equal(500, options.BackfillFor(CandleInterval.FourHours));
        Assert.Equal(400, options.BackfillFor(CandleInterval.OneDay));
        Assert.Equal(200, options.BackfillFor(CandleInterval.OneWeek));
        Assert.Equal(360, options.SymbolRefreshIntervalMinutes);
        Assert.Equal(TimeSpan.FromHours(6), options.SymbolRefreshInterval);
        Assert.Equal(["15m", "1H", "4H", "1D", "1W"], MarketDataOptions.Intervals.Select(MarketDataOptions.KeyOf));
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new()
        {
            ["MarketData:BackfillCandles:1h"] = "1200",
            ["MarketData:BackfillCandles:1W"] = "60",
            ["MarketData:SymbolRefreshIntervalMinutes"] = "1440",
        });

        Assert.Equal(1200, options.BackfillFor(CandleInterval.OneHour));
        Assert.Equal(60, options.BackfillFor(CandleInterval.OneWeek));
        Assert.Equal(500, options.BackfillFor(CandleInterval.FifteenMinutes));
        Assert.Equal(TimeSpan.FromDays(1), options.SymbolRefreshInterval);
    }

    [Theory]
    [InlineData("MarketData:BackfillCandles:15m", "19", "MarketData:BackfillCandles:15m must be between 20 and 5000")]
    [InlineData("MarketData:BackfillCandles:1D", "5001", "MarketData:BackfillCandles:1D must be between 20 and 5000")]
    [InlineData("MarketData:BackfillCandles:5m", "100", "MarketData:BackfillCandles:5m is not a candle interval; use 15m, 1H, 4H, 1D, 1W")]
    [InlineData("MarketData:SymbolRefreshIntervalMinutes", "0", "MarketData:SymbolRefreshIntervalMinutes")]
    [InlineData("MarketData:SymbolRefreshIntervalMinutes", "1441", "MarketData:SymbolRefreshIntervalMinutes")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void AMissingIntervalIsReported()
    {
        var options = new MarketDataOptions();
        options.BackfillCandles.Remove("1W");

        var result = new MarketDataOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal("MarketData:BackfillCandles:1W must be set.", Assert.Single(result.Failures!));
    }

    private static MarketDataOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = MarketDataTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
    }
}
