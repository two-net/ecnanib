using Ecnanib.Council.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Council.Configuration;

public class CouncilOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("Council", CouncilOptions.SectionName);
        Assert.Equal(60, options.RoleTimeoutSeconds);
        Assert.Equal(45, options.RiskManagerReserveSeconds);
        Assert.Equal(5, options.MinimumRoleBudgetSeconds);
        Assert.Equal(30, options.RecentCandles);
        Assert.Equal(10, options.HigherTimeframeCandles);
        Assert.Equal(8192, options.MaxOutputTokens);
        Assert.Equal(1200, options.MaxAssessmentLength);
        Assert.Equal(600, options.MaxRationaleLength);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RoleTimeout);
        Assert.Equal(10, options.ExecutionReserveSeconds);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ExecutionReserve);
        Assert.Equal(TimeSpan.FromSeconds(2), CouncilOptions.DeadlineTolerance);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Council:RoleTimeoutSeconds"] = "120", ["Council:RecentCandles"] = "12", ["Council:HigherTimeframeCandles"] = "0", ["Council:ExecutionReserveSeconds"] = "4" });

        Assert.Equal(TimeSpan.FromSeconds(120), options.RoleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(4), options.ExecutionReserve);
        Assert.Equal(12, options.RecentCandles);
        Assert.Equal(0, options.HigherTimeframeCandles);
    }

    [Theory]
    [InlineData("Council:RoleTimeoutSeconds", "0", "Council:RoleTimeoutSeconds")]
    [InlineData("Council:RoleTimeoutSeconds", "4", "RoleTimeoutSeconds must be at least the minimum role budget")]
    [InlineData("Council:RiskManagerReserveSeconds", "-1", "Council:RiskManagerReserveSeconds")]
    [InlineData("Council:RiskManagerReserveSeconds", "2", "RiskManagerReserveSeconds must be at least the minimum role budget")]
    [InlineData("Council:MinimumRoleBudgetSeconds", "0", "Council:MinimumRoleBudgetSeconds")]
    [InlineData("Council:ExecutionReserveSeconds", "0", "Council:ExecutionReserveSeconds")]
    [InlineData("Council:ExecutionReserveSeconds", "86", "shortest Agents:EvaluationDeadlineSeconds (90 s)")]
    [InlineData("Council:RecentCandles", "0", "Council:RecentCandles")]
    [InlineData("Council:MaxOutputTokens", "0", "Council:MaxOutputTokens")]
    [InlineData("Council:MaxAssessmentLength", "10", "MaxAssessmentLength")]
    [InlineData("Council:MaxRationaleLength", "4000", "MaxRationaleLength")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static CouncilOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = CouncilTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<CouncilOptions>>().Value;
    }
}
