using Ecnanib.Accounts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Accounts.Configuration;

public class AccountsOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal(TimeSpan.FromDays(30), options.SessionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), options.SessionRevalidation);
        Assert.Equal(10, options.SignInAttemptsPerWindow);
        Assert.Equal(100, options.SignInAttemptsPerAddress);
        Assert.Equal(TimeSpan.FromMinutes(15), options.SignInWindow);
        Assert.Equal(8, options.MinPasswordLength);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Accounts:SessionDays"] = "7", ["Accounts:MinPasswordLength"] = "12", ["Accounts:SignInAttemptsPerWindow"] = "3" });

        Assert.Equal(TimeSpan.FromDays(7), options.SessionLifetime);
        Assert.Equal(12, options.MinPasswordLength);
        Assert.Equal(3, options.SignInAttemptsPerWindow);
        Assert.Equal(100, options.SignInAttemptsPerAddress);
    }

    [Theory]
    [InlineData("Accounts:SessionDays", "0", "SessionDays")]
    [InlineData("Accounts:SessionDays", "3651", "SessionDays")]
    [InlineData("Accounts:SessionRevalidationMinutes", "0", "SessionRevalidationMinutes")]
    [InlineData("Accounts:SignInAttemptsPerWindow", "0", "SignInAttemptsPerWindow")]
    [InlineData("Accounts:SignInAttemptsPerAddress", "5", "Accounts:SignInAttemptsPerAddress must be >= SignInAttemptsPerWindow")]
    [InlineData("Accounts:SignInWindowMinutes", "0", "SignInWindowMinutes")]
    [InlineData("Accounts:MinPasswordLength", "7", "MinPasswordLength")]
    [InlineData("Accounts:MinPasswordLength", "65", "MinPasswordLength")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void RevalidationMustBeShorterThanTheSession()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { ["Accounts:SessionDays"] = "1", ["Accounts:SessionRevalidationMinutes"] = "1440" }));

        Assert.Contains("Accounts:SessionRevalidationMinutes must be shorter than the session", ex.Message);
    }

    private static AccountsOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = AccountsTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<AccountsOptions>>().Value;
    }
}
