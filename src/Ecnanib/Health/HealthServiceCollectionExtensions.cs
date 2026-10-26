using Ecnanib.Health.Abstractions;
using Ecnanib.Health.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Health;

public static class HealthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the health module: <see cref="ISystemHealth"/>, which gathers database connectivity and the instance
    /// lock, the exchange streams, the agent scheduler's status, the exchange reconciliation and the last model sync
    /// per provider into one report (NFR-OBS-01). Has no configuration section. Requires <c>AddDatabase</c>,
    /// <c>AddExchange</c>, <c>AddAgents</c> and <c>AddModelSync</c>, resolved when a report is asked for. Connects to
    /// nothing at registration.
    /// </summary>
    public static IServiceCollection AddHealth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISystemHealth, SystemHealth>();
        return services;
    }
}
