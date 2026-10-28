using Ecnanib.Database;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Database;

/// <summary>Builds the database module as the application registers it, against any connection string.</summary>
internal static class DatabaseTestHost
{
    public const string OfflineConnectionString = "Host=localhost;Database=ecnanib_test;Username=ecnanib;Password=hunter2";

    /// <summary>The one key every test host and seed encrypts the stored keys under (FR-KEY-04); the databases are throwaway.</summary>
    public static readonly string EncryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    /// <summary>A second key, for the rotation and the wrong-key cases.</summary>
    public static readonly string OtherEncryptionKey = Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What a host needs to bind and validate the database section: the connection string and the encryption key.</summary>
    public static Dictionary<string, string?> OfflineConfiguration(string? connectionString = OfflineConnectionString) => new()
    {
        ["Database:ConnectionString"] = connectionString,
        ["Database:EncryptionKey"] = EncryptionKey,
    };

    public static ServiceProvider BuildServices(string? connectionString = OfflineConnectionString, TestLoggerProvider? logs = null, Dictionary<string, string?>? config = null, TimeProvider? time = null, Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?> { ["Database:EncryptionKey"] = EncryptionKey };
        if (connectionString is not null)
            values["Database:ConnectionString"] = connectionString;
        foreach (var (key, value) in config ?? [])
            values[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        if (logs is not null)
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        if (time is not null)
            services.AddSingleton(time);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
