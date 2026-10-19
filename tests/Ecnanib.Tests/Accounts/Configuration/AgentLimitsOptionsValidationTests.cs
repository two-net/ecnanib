using Ecnanib.Accounts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Accounts.Configuration;

public class AgentLimitsOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal("Agents", AgentLimitsOptions.SectionName);
        Assert.Equal(10, options.MaxAgentsPerUser);
        Assert.Equal(3, options.MaxRunningAgentsPerUser);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Agents:MaxAgentsPerUser"] = "2", ["Agents:MaxRunningAgentsPerUser"] = "1" });

        Assert.Equal(2, options.MaxAgentsPerUser);
        Assert.Equal(1, options.MaxRunningAgentsPerUser);
    }

    [Theory]
    [InlineData("Agents:MaxAgentsPerUser", "0", "MaxAgentsPerUser")]
    [InlineData("Agents:MaxRunningAgentsPerUser", "0", "MaxRunningAgentsPerUser")]
    [InlineData("Agents:MaxRunningAgentsPerUser", "11", "Agents:MaxRunningAgentsPerUser must be <= Agents:MaxAgentsPerUser")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    private static AgentLimitsOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = AccountsTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<AgentLimitsOptions>>().Value;
    }
}
