using Ecnanib.Database.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Database.Configuration;

public class DatabaseOptionsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve([]);

        Assert.Equal(DatabaseTestHost.OfflineConnectionString, options.ConnectionString);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CommandTimeout);
        Assert.Equal(3, options.RetryOnFailureCount);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Database:CommandTimeoutSeconds"] = "90", ["Database:RetryOnFailureCount"] = "0" });

        Assert.Equal(TimeSpan.FromSeconds(90), options.CommandTimeout);
        Assert.Equal(0, options.RetryOnFailureCount);
    }

    [Theory]
    [InlineData("Database:ConnectionString", "", "Database:ConnectionString is required")]
    [InlineData("Database:ConnectionString", "Host=localhost;Username=u", "Database:ConnectionString must name a Database")]
    [InlineData("Database:ConnectionString", "Database=ecnanib;Username=u", "Database:ConnectionString must name a Host")]
    [InlineData("Database:ConnectionString", "Host=localhost;Database=x;Bogus=1", "Database:ConnectionString is not a valid PostgreSQL connection string")]
    [InlineData("Database:CommandTimeoutSeconds", "0", "CommandTimeoutSeconds")]
    [InlineData("Database:CommandTimeoutSeconds", "601", "CommandTimeoutSeconds")]
    [InlineData("Database:RetryOnFailureCount", "-1", "RetryOnFailureCount")]
    public void InvalidValuesFailWithPropertyPath(string key, string value, string expectedFragment)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void FailureMessagesNeverEchoThePassword()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["Database:ConnectionString"] = "Host=localhost;Password=hunter2;Bogus=1",
        }));

        Assert.DoesNotContain("hunter2", ex.Message);
    }

    private static DatabaseOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = DatabaseTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    }
}
