using Ecnanib.Keys.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Keys.Configuration;

public class KeysOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal(TimeSpan.FromMinutes(60), options.RevalidationInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ValidationTimeout);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Keys:RevalidationIntervalMinutes"] = "15", ["Keys:ValidationTimeoutSeconds"] = "20" });

        Assert.Equal(TimeSpan.FromMinutes(15), options.RevalidationInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), options.ValidationTimeout);
    }

    [Theory]
    [InlineData("Keys:RevalidationIntervalMinutes", "0", "RevalidationIntervalMinutes")]
    [InlineData("Keys:RevalidationIntervalMinutes", "10081", "RevalidationIntervalMinutes")]
    [InlineData("Keys:ValidationTimeoutSeconds", "4", "ValidationTimeoutSeconds")]
    [InlineData("Keys:ValidationTimeoutSeconds", "601", "ValidationTimeoutSeconds")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void TheTimeoutMustBeShorterThanTheInterval()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { ["Keys:RevalidationIntervalMinutes"] = "1", ["Keys:ValidationTimeoutSeconds"] = "60" }));

        Assert.Contains("Keys:ValidationTimeoutSeconds must be shorter than the revalidation interval", ex.Message);
    }

    private static KeysOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = KeysTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<KeysOptions>>().Value;
    }
}
