using Ecnanib.Experience.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Experience.Configuration;

public class ExperienceOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("Experience", ExperienceOptions.SectionName);
        Assert.Equal(15, options.RefreshIntervalMinutes);
        Assert.Equal(TimeSpan.FromMinutes(15), options.RefreshInterval);
        Assert.Equal(30, options.ShadowHorizonCandles);
        Assert.Equal(5, options.RecentOutcomes);
        Assert.Equal(10, options.MinimumTradesForSuggestion);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new()
        {
            ["Experience:RefreshIntervalMinutes"] = "60",
            ["Experience:ShadowHorizonCandles"] = "50",
            ["Experience:RecentOutcomes"] = "8",
            ["Experience:MinimumTradesForSuggestion"] = "25",
        });

        Assert.Equal(TimeSpan.FromHours(1), options.RefreshInterval);
        Assert.Equal(50, options.ShadowHorizonCandles);
        Assert.Equal(8, options.RecentOutcomes);
        Assert.Equal(25, options.MinimumTradesForSuggestion);
    }

    [Theory]
    [InlineData("Experience:RefreshIntervalMinutes", "0", "Experience:RefreshIntervalMinutes")]
    [InlineData("Experience:RefreshIntervalMinutes", "1441", "Experience:RefreshIntervalMinutes")]
    [InlineData("Experience:ShadowHorizonCandles", "0", "Experience:ShadowHorizonCandles")]
    [InlineData("Experience:ShadowHorizonCandles", "201", "Experience:ShadowHorizonCandles")]
    [InlineData("Experience:RecentOutcomes", "0", "Experience:RecentOutcomes")]
    [InlineData("Experience:RecentOutcomes", "21", "Experience:RecentOutcomes")]
    [InlineData("Experience:MinimumTradesForSuggestion", "0", "Experience:MinimumTradesForSuggestion")]
    [InlineData("Experience:MinimumTradesForSuggestion", "1001", "Experience:MinimumTradesForSuggestion")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static ExperienceOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = ExperienceTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<ExperienceOptions>>().Value;
    }
}
