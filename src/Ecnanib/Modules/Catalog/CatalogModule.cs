using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddSingleton<IReferenceDataSeeder, ProviderSeeder>();
        return services;
    }
}
