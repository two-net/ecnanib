using Ecnanib.Ai.Configuration;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.ModelSync;

public static class ModelSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the model-sync module: <see cref="IProviderCatalogSync"/>, the catalog upsert that key validation
    /// feeds (FR-SYNC-02); <see cref="IModelSyncService"/>, the sync on demand; <see cref="IModelCatalog"/>, the
    /// catalog and the last sync per provider (FR-AI-03, FR-SYNC-06, NFR-OBS-01); and the scheduled sync, one hosted
    /// service (FR-SYNC-02, FR-SYNC-03). Binds the <c>Providers</c> section for its interval and miss threshold;
    /// <c>AddAiProviders</c> validates it. Requires <c>AddDatabase</c> and <c>AddAiProviders</c>. Connects to nothing
    /// at registration.
    /// </summary>
    public static IServiceCollection AddModelSync(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName));
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProviderCatalogSync, ProviderCatalogSync>();
        services.TryAddSingleton<IModelCatalog, ModelCatalog>();
        services.TryAddSingleton<ModelSyncRunner>();
        services.TryAddSingleton<IModelSyncService>(sp => sp.GetRequiredService<ModelSyncRunner>());
        services.TryAddSingleton<ModelSyncScheduler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ModelSyncScheduler>(sp => sp.GetRequiredService<ModelSyncScheduler>()));
        return services;
    }
}
