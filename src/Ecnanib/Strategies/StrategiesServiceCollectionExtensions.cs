using Ecnanib.Agents.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Strategies;

public static class StrategiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Appendix A strategies (FR-STR-01..07): the <see cref="IStrategyCatalog"/> and the deterministic
    /// <see cref="IStrategyEngine"/>, both with <c>TryAdd</c> so a registration made earlier wins. Needs no
    /// configuration section; register before <c>AddAgents</c>, which requires the catalog.
    /// </summary>
    public static IServiceCollection AddStrategies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStrategy, IntradayTrendPullbackStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStrategy, TrendPullbackStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStrategy, RangeBreakoutRetestStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStrategy, SwingPullbackStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStrategy, ConsolidationBreakoutStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStrategy, PositionBreakoutStrategy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStrategy, EmaPullbackStrategy>());
        services.TryAddSingleton<IStrategyCatalog, StrategyCatalog>();
        services.TryAddSingleton<IStrategyEngine, StrategyEngine>();
        return services;
    }
}
