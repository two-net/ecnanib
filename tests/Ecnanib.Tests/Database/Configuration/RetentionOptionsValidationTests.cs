using Ecnanib.Database.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Database.Configuration;

public class RetentionOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal(TimeSpan.FromDays(30), options.LogRetention);
        Assert.Equal(TimeSpan.FromDays(365), options.CandleRetention);
        Assert.Equal(TimeSpan.FromDays(90), options.DeliberationTranscriptRetention);
        Assert.Equal(TimeSpan.FromMinutes(60), options.SweepInterval);
        Assert.Equal(5000, options.DeleteBatchSize);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Retention:LogDays"] = "7", ["Retention:SweepIntervalMinutes"] = "5" });

        Assert.Equal(TimeSpan.FromDays(7), options.LogRetention);
        Assert.Equal(TimeSpan.FromMinutes(5), options.SweepInterval);
    }

    [Theory]
    [InlineData("Retention:LogDays", "0", "LogDays")]
    [InlineData("Retention:CandleDays", "0", "CandleDays")]
    [InlineData("Retention:DeliberationTranscriptDays", "-1", "DeliberationTranscriptDays")]
    [InlineData("Retention:SweepIntervalMinutes", "0", "SweepIntervalMinutes")]
    [InlineData("Retention:DeleteBatchSize", "50", "DeleteBatchSize")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static RetentionOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = DatabaseTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<RetentionOptions>>().Value;
    }
}
