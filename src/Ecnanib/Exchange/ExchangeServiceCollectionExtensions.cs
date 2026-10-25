using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Binance;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Exchange.Streaming;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange;

public static class ExchangeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the exchange module: validated <see cref="ExchangeOptions"/>, Binance clients, market and user-data
    /// streams and health. Nothing contacts the exchange until a client or stream is first used.
    /// </summary>
    public static IServiceCollection AddExchange(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ExchangeOptions>()
            .Bind(configuration.GetSection(ExchangeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ExchangeOptions>, ExchangeOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(BinanceClientRegistry.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value.RequestTimeout)
            // FR-LOG-05: the factory's logging handler would otherwise print the API key header at Trace.
            .RedactLoggedHeaders(["X-MBX-APIKEY"]);

        services.TryAddSingleton<BinanceEnvironmentFactory>();
        services.TryAddSingleton<IBinanceClientRegistry, BinanceClientRegistry>();
        services.TryAddSingleton<ExchangeRetryPolicy>();

        services.TryAddSingleton<IMarketStreamSource, BinanceMarketStreamSource>();
        services.TryAddSingleton<IUserDataSource, BinanceUserDataSource>();
        services.TryAddSingleton<MarketStreamHub>();
        services.TryAddSingleton<IMarketStreams>(sp => sp.GetRequiredService<MarketStreamHub>());
        services.TryAddSingleton<UserDataStreamHub>();
        services.TryAddSingleton<IUserDataStreams>(sp => sp.GetRequiredService<UserDataStreamHub>());
        services.TryAddSingleton<BinanceRateLimitMonitor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MarketStreamHub>(sp => sp.GetRequiredService<MarketStreamHub>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, UserDataStreamHub>(sp => sp.GetRequiredService<UserDataStreamHub>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BinanceRateLimitMonitor>(sp => sp.GetRequiredService<BinanceRateLimitMonitor>()));

        services.TryAddSingleton<IExchangeClientFactory, BinanceClientFactory>();
        services.TryAddSingleton<IExchangeHealth, ExchangeHealth>();

        return services;
    }
}
