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
        Assert.Equal(DatabaseTestHost.EncryptionKey, options.EncryptionKey);
        Assert.Null(options.PreviousEncryptionKey);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CommandTimeout);
        Assert.Equal(3, options.RetryOnFailureCount);
        Assert.Equal(TimeSpan.FromSeconds(30), options.InstanceLockWait);
    }

    [Fact]
    public void OverridesAreHonoured()
    {
        var options = Resolve(new() { ["Database:CommandTimeoutSeconds"] = "90", ["Database:RetryOnFailureCount"] = "0", ["Database:PreviousEncryptionKey"] = DatabaseTestHost.OtherEncryptionKey });

        Assert.Equal(TimeSpan.FromSeconds(90), options.CommandTimeout);
        Assert.Equal(0, options.RetryOnFailureCount);
        Assert.Equal(DatabaseTestHost.OtherEncryptionKey, options.PreviousEncryptionKey);
    }

    [Theory]
    [InlineData("Database:ConnectionString", "", "Database:ConnectionString is required")]
    [InlineData("Database:ConnectionString", "Host=localhost;Username=u", "Database:ConnectionString must name a Database")]
    [InlineData("Database:ConnectionString", "Database=ecnanib;Username=u", "Database:ConnectionString must name a Host")]
    [InlineData("Database:ConnectionString", "Host=localhost;Database=x;Bogus=1", "Database:ConnectionString is not a valid PostgreSQL connection string")]
    [InlineData("Database:EncryptionKey", "", "Database:EncryptionKey is required: the base64 encoding of 32 random bytes (openssl rand -base64 32).")]
    [InlineData("Database:EncryptionKey", "not base64!", "Database:EncryptionKey must be the base64 encoding of exactly 32 bytes.")]
    [InlineData("Database:EncryptionKey", "AQIDBAUGBwgJCgsMDQ4PEA==", "Database:EncryptionKey must be the base64 encoding of exactly 32 bytes.")]
    [InlineData("Database:PreviousEncryptionKey", "AQIDBAUGBwgJCgsMDQ4PEA==", "Database:PreviousEncryptionKey must be the base64 encoding of exactly 32 bytes.")]
    [InlineData("Database:CommandTimeoutSeconds", "0", "CommandTimeoutSeconds")]
    [InlineData("Database:CommandTimeoutSeconds", "601", "CommandTimeoutSeconds")]
    [InlineData("Database:RetryOnFailureCount", "-1", "RetryOnFailureCount")]
    [InlineData("Database:InstanceLockWaitSeconds", "601", "InstanceLockWaitSeconds")]
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

    /// <summary>FR-CFG-03: a previous key equal to the current one is a rotation that has already run, and is refused rather than silently ignored.</summary>
    [Fact]
    public void APreviousKeyEqualToTheCurrentOneIsRefused()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { ["Database:PreviousEncryptionKey"] = DatabaseTestHost.EncryptionKey }));

        Assert.Contains("Database:PreviousEncryptionKey must differ from Database:EncryptionKey; remove it once the rotation has run.", ex.Message);
    }

    /// <summary>FR-LOG-05: a refused key is never quoted back.</summary>
    [Fact]
    public void FailureMessagesNeverEchoTheKeys()
    {
        const string sixteenBytes = "AQIDBAUGBwgJCgsMDQ4PEA==";
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["Database:EncryptionKey"] = sixteenBytes,
            ["Database:PreviousEncryptionKey"] = sixteenBytes,
        }));

        Assert.DoesNotContain(sixteenBytes, ex.Message);
        Assert.DoesNotContain(DatabaseTestHost.EncryptionKey, ex.Message);
    }

    private static DatabaseOptions Resolve(Dictionary<string, string?> values)
    {
        using var provider = DatabaseTestHost.BuildServices(config: values);
        return provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    }
}
