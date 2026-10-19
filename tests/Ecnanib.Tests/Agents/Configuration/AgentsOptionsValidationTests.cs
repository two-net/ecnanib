using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Agents.Configuration;

public class AgentsOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("Agents", AgentsOptions.SectionName);
        Assert.Equal(TimeSpan.FromSeconds(90), options.DeadlineFor(CandleInterval.FifteenMinutes));
        Assert.Equal(TimeSpan.FromSeconds(180), options.DeadlineFor(CandleInterval.OneHour));
        Assert.Equal(TimeSpan.FromSeconds(300), options.DeadlineFor(CandleInterval.FourHours));
        Assert.Equal(TimeSpan.FromSeconds(300), options.DeadlineFor(CandleInterval.OneDay));
        Assert.Equal(TimeSpan.FromSeconds(300), options.LongestDeadline);
        Assert.Equal(10, options.MaxConcurrentEvaluations);
        Assert.Equal(1, options.DefaultEntryTimeoutCandles);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new()
        {
            ["Agents:EvaluationDeadlineSeconds:15m"] = "60",
            ["Agents:MaxConcurrentEvaluations"] = "2",
            ["Agents:DefaultEntryTimeoutCandles"] = "3",
        });

        Assert.Equal(TimeSpan.FromSeconds(60), options.DeadlineFor(CandleInterval.FifteenMinutes));
        Assert.Equal(TimeSpan.FromSeconds(180), options.DeadlineFor(CandleInterval.OneHour));
        Assert.Equal(2, options.MaxConcurrentEvaluations);
        Assert.Equal(3, options.DefaultEntryTimeoutCandles);
    }

    [Theory]
    [InlineData("Agents:EvaluationDeadlineSeconds:15m", "0", "Agents:EvaluationDeadlineSeconds:15m must be > 0")]
    [InlineData("Agents:EvaluationDeadlineSeconds:15m", "900", "must be shorter than the timeframe")]
    [InlineData("Agents:EvaluationDeadlineSeconds:2H", "10", "is not a timeframe")]
    [InlineData("Agents:MaxConcurrentEvaluations", "0", "MaxConcurrentEvaluations")]
    [InlineData("Agents:DefaultEntryTimeoutCandles", "0", "DefaultEntryTimeoutCandles")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void TheTimeframeKeysAreTheSrsSpelling()
    {
        Assert.Equal(["15m", "1H", "4H", "1D"], AgentsOptions.Timeframes.Select(AgentsOptions.KeyOf));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentsOptions.KeyOf(CandleInterval.OneWeek));
    }

    private static AgentsOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = AgentsTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<AgentsOptions>>().Value;
    }
}
