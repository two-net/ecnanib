using Ecnanib.Accounts;
using Ecnanib.Database;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Accounts;

/// <summary>Builds the database and accounts modules as the application registers them, against any connection string.</summary>
internal static class AccountsTestHost
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ServiceProvider BuildServices(string? connectionString = DatabaseTestHost.OfflineConnectionString, TestLoggerProvider? logs = null, Dictionary<string, string?>? config = null, TimeProvider? time = null, string? environment = null, Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?>();
        if (connectionString is not null)
            values["Database:ConnectionString"] = connectionString;
        foreach (var (key, value) in config ?? [])
            values[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        if (environment is not null)
            services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environment));
        services.AddDatabase(configuration);
        services.AddAccounts(configuration);
        if (logs is not null)
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        if (time is not null)
            services.AddSingleton(time);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Ecnanib.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
