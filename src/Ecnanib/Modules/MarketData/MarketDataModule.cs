using Ecnanib.Modules.MarketData.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.MarketData;

public static class MarketDataModule
{
    public static IServiceCollection AddMarketData(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CandleFeed>();
        services.AddSingleton<QuoteFeed>();
        services.AddSingleton<IMarketData, MarketSnapshots>();
        services.AddSingleton<IExchangeSymbols, ExchangeSymbols>();

        return services;
    }
}
