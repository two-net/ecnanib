using System.Net;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.ExchangeAdapter;

public static class ExchangeAdapterModule
{
    public static IServiceCollection AddExchangeAdapter(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => BinanceSettings.Read(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IHostEnvironment>()));
        services.AddHostedService<ExchangeAdapterStartup>();

        // No redirects, or a response could send a signed request somewhere else. No logging of its
        // own either: the factory's handlers would log the whole URI, signature and all (FR-LOG-08).
        // Timeouts are per request, in BinanceTransport.
        services.AddHttpClient(BinanceTransport.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseCookies = false,
            })
            .RemoveAllLoggers();

        services.AddSingleton<BinanceServers>();
        services.AddSingleton<IExchangeUsage>(provider => provider.GetRequiredService<BinanceServers>());
        services.AddSingleton<BinanceTransport>();
        services.AddSingleton<ExchangeInfoCache>();
        services.AddSingleton<IExchangeMarketData, BinanceMarketData>();
        services.AddSingleton<IExchangeAccount, BinanceAccount>();
        services.AddSingleton<IExchangeTrading, BinanceTrading>();

        return services;
    }
}
