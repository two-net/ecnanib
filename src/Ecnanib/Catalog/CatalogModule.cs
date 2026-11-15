using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.Catalog;

/// <summary>Catalog: model sync and eligibility (SRS 3.1). See docs/catalog.md.</summary>
public static class CatalogModule
{
    /// <summary>
    /// Registers the component: what syncs the catalog from the providers' lists of their
    /// models, and the job that does so on the schedule (FR-CAT-1, FR-CAT-2).
    /// </summary>
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        // One object syncs for the job, for a request, and for a user's key that its provider
        // took, so that it knows of each of them what the others did.
        services.AddSingleton<CatalogSync>();
        services.AddSingleton<ICatalogSync>(provider => provider.GetRequiredService<CatalogSync>());

        services.AddBackgroundJob<CatalogSyncJob>();

        return services;
    }
}
