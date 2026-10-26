using Ecnanib.Execution.Configuration;
using Ecnanib.Tests.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Execution.Configuration;

public class ExecutionOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("Execution", ExecutionOptions.SectionName);
        Assert.Equal(3, options.ProtectiveOrderAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), options.ProtectiveOrderRetryDelay);
        Assert.Equal(0.005m, options.SpotStopLimitOffsetRate);
        Assert.Equal(TimeSpan.FromSeconds(20), options.ManageBudget);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ReconcileAfter);
        Assert.Equal(TimeSpan.FromSeconds(6), options.FillTrackerStartDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), options.LostAnswerLookup);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Execution:ProtectiveOrderAttempts"] = "5", ["Execution:SpotStopLimitOffsetPercent"] = "1", ["Execution:ManageBudgetSeconds"] = "10" });

        Assert.Equal(5, options.ProtectiveOrderAttempts);
        Assert.Equal(0.01m, options.SpotStopLimitOffsetRate);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ManageBudget);
    }

    [Theory]
    [InlineData("Execution:ProtectiveOrderAttempts", "0", "Execution:ProtectiveOrderAttempts must be between 1 and 10")]
    [InlineData("Execution:ProtectiveOrderAttempts", "11", "Execution:ProtectiveOrderAttempts must be between 1 and 10")]
    [InlineData("Execution:ProtectiveOrderRetryDelayMs", "50", "Execution:ProtectiveOrderRetryDelayMs must be between 100 and 30000")]
    [InlineData("Execution:SpotStopLimitOffsetPercent", "0.01", "Execution:SpotStopLimitOffsetPercent must be between 0.05 and 5")]
    [InlineData("Execution:SpotStopLimitOffsetPercent", "6", "Execution:SpotStopLimitOffsetPercent must be between 0.05 and 5")]
    [InlineData("Execution:ManageBudgetSeconds", "4", "Execution:ManageBudgetSeconds must be between 5 and 60")]
    [InlineData("Execution:ReconcileAfterSeconds", "601", "Execution:ReconcileAfterSeconds must be between 5 and 600")]
    [InlineData("Execution:FillTrackerStartDelaySeconds", "-1", "Execution:FillTrackerStartDelaySeconds must be between 0 and 3600")]
    [InlineData("Execution:LostAnswerLookupSeconds", "0", "Execution:LostAnswerLookupSeconds must be between 1 and 60")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static ExecutionOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = AgentsTestHost.BuildServices(config: values, execution: true);
        return provider.GetRequiredService<IOptions<ExecutionOptions>>().Value;
    }
}
