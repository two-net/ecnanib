using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys;
using Ecnanib.ModelSync;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Keys;

/// <summary>Builds the database, model-sync and keys modules as the application registers them, with fakes standing in for the exchange and AI modules.</summary>
internal static class KeysTestHost
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ServiceProvider BuildServices(
        string? connectionString = DatabaseTestHost.OfflineConnectionString,
        TestLoggerProvider? logs = null,
        Dictionary<string, string?>? config = null,
        TimeProvider? time = null,
        FakeExchangeClientFactory? exchange = null,
        FakeAiClientFactory? ai = null,
        Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?>();
        if (connectionString is not null)
            values["Database:ConnectionString"] = connectionString;
        foreach (var (key, value) in config ?? [])
            values[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddModelSync(configuration);
        services.AddKeys(configuration);
        services.AddSingleton<IExchangeClientFactory>(exchange ?? new FakeExchangeClientFactory());
        services.AddSingleton<IAiClientFactory>(ai ?? new FakeAiClientFactory());
        if (logs is not null)
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        if (time is not null)
            services.AddSingleton(time);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
