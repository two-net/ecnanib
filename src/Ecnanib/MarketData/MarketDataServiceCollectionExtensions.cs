using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Configuration;
using Ecnanib.MarketData.Abstractions;
using Ecnanib.MarketData.Configuration;
using Ecnanib.MarketData.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.MarketData;

public static class MarketDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the market-data module: validated <see cref="MarketDataOptions"/>; the <see cref="ISymbolCatalog"/>
    /// with its listing per environment and market (FR-DATA-01) and the <see cref="SymbolCatalogRefresher"/> hosted
    /// service that keeps it fresh; the <see cref="IMarketDataLoader"/> that backfills on start, follows the candle
    /// streams and persists closed candles for audit (FR-DATA-02, FR-DATA-03, FR-DATA-05); and the
    /// <see cref="IFuturesMetrics"/> streamed per futures symbol (FR-DATA-04). The catalog and the loader replace the
    /// agents module's defaults, so register it <b>before</b> <c>AddAgents</c>. Requires <c>AddExchange</c> and
    /// <c>AddDatabase</c> (whose <c>Retention</c> section is bound here for the candle retention period and validated
    /// there). Connects to nothing at registration.
    /// </summary>
    public static IServiceCollection AddMarketData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MarketDataOptions>()
            .Bind(configuration.GetSection(MarketDataOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MarketDataOptions>, MarketDataOptionsValidator>());
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection(RetentionOptions.SectionName));

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<SymbolCatalog>();
        services.TryAddSingleton<ISymbolCatalog>(sp => sp.GetRequiredService<SymbolCatalog>());
        services.TryAddSingleton<FuturesMetrics>();
        services.TryAddSingleton<IFuturesMetrics>(sp => sp.GetRequiredService<FuturesMetrics>());
        services.TryAddSingleton<CandleStore>();
        services.TryAddSingleton<CandleFeed>();
        services.TryAddSingleton<IMarketDataLoader>(sp => sp.GetRequiredService<CandleFeed>());

        services.TryAddSingleton<SymbolCatalogRefresher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SymbolCatalogRefresher>(sp => sp.GetRequiredService<SymbolCatalogRefresher>()));
        return services;
    }
}
