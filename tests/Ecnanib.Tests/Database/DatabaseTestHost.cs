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

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ServiceProvider BuildServices(string? connectionString = OfflineConnectionString, TestLoggerProvider? logs = null, Dictionary<string, string?>? config = null, TimeProvider? time = null, Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?>();
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
