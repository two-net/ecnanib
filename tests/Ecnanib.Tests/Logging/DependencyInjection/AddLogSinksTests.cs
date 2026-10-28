using Ecnanib.Database;
using Ecnanib.Logging;
using Ecnanib.Logging.Configuration;
using Ecnanib.Logging.Services;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;

namespace Ecnanib.Tests.Logging.DependencyInjection;

public class AddLogSinksTests
{
    [Fact]
    public async Task RegistersSerilogTheSinkAndItsHostedWriterWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = Build(factory: factory);

        Assert.IsType<SerilogLoggerFactory>(provider.GetRequiredService<ILoggerFactory>());
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<DatabaseLogWriter>());
        Assert.Same(hosted, provider.GetRequiredService<DatabaseLogWriter>());
        Assert.Single(provider.GetServices<IHostedService>(), h => h.GetType().Namespace!.StartsWith("Ecnanib.Logging", StringComparison.Ordinal));
        Assert.Contains(provider.GetServices<IValidateOptions<LoggingOptions>>(), v => v is LoggingOptionsValidator);
        provider.GetRequiredService<IStartupValidator>().Validate();

        var logger = provider.GetRequiredService<ILogger<AddLogSinksTests>>();
        logger.LogInformation("queued for the database");
        logger.LogDebug("below both minimums");

        Assert.Equal(1, provider.GetRequiredService<DatabaseLogSink>().Reader.Count);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task AnInvalidSectionStillBuildsTheConsoleLoggerAndFailsAtStartupValidation()
    {
        await using var provider = Build(new() { ["Logging:LogLevel:Default"] = "Information" });

        var logger = provider.GetRequiredService<ILogger<AddLogSinksTests>>();
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        logger.LogInformation("console only while the section is invalid");
        Assert.Equal(0, provider.GetRequiredService<DatabaseLogSink>().Reader.Count);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Logging:LogLevel:Default is not used", ex.Message);
    }

    /// <summary>C-08: the providers the default host builder registers (console, debug, event source) go; the console and the database sink are the only outputs.</summary>
    [Fact]
    public void RemovesTheProvidersRegisteredBeforeIt()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(DatabaseTestHost.OfflineConfiguration()).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddDebug());
        Assert.Contains(services, d => d.ServiceType == typeof(ILoggerProvider));

        services.AddLogSinks(configuration);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILoggerProvider));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SerilogLoggerFactory>(provider.GetRequiredService<ILoggerFactory>());
    }

    private static ServiceProvider Build(Dictionary<string, string?>? values = null, ThrowingDbContextFactory? factory = null)
    {
        var all = DatabaseTestHost.OfflineConfiguration();
        foreach (var (key, value) in values ?? [])
            all[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(all).Build();
        var services = new ServiceCollection();
        services.AddLogSinks(configuration);
        services.AddDatabase(configuration);
        if (factory is not null)
            services.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
