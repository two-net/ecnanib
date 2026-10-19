using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.ModelSync;

public static class ModelSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the model-sync module: today only <see cref="IProviderCatalogSync"/>, the catalog upsert that key
    /// validation feeds (FR-SYNC-02). The scheduled sync, deprecation and the providers page bind their options from
    /// <paramref name="configuration"/> when they arrive. Requires <c>AddDatabase</c>. Adds no hosted service and
    /// connects to nothing.
    /// </summary>
    public static IServiceCollection AddModelSync(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProviderCatalogSync, ProviderCatalogSync>();
        return services;
    }
}
