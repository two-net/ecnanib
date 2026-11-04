using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddSingleton(provider => CatalogSettings.Read(provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IAiProviders>()));
        services.AddHostedService<CatalogStartup>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICatalog, Catalog>();
        services.AddSingleton<CatalogSync>();
        services.AddSingleton<ICatalogSync>(provider => provider.GetRequiredService<CatalogSync>());
        services.AddSingleton<ICatalogAdministration>(provider => provider.GetRequiredService<CatalogSync>());
        services.AddSingleton<IRecurringJob, CatalogSyncJob>();
        services.AddSingleton<ICredentialObserver, CatalogCredentialObserver>();
        services.AddSingleton<IReferenceDataSeeder, ProviderSeeder>();
        return services;
    }
}
