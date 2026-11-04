namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Says at startup how often the catalog syncs and how many models the configuration describes. The host
/// builds every hosted service before it starts any, and this one takes <see cref="CatalogSettings"/>, so an
/// invalid setting stops startup before any migration runs (CFG-03).
/// </summary>
internal sealed class CatalogStartup(CatalogSettings settings, ILogger<CatalogStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "The model catalog syncs every {SyncIntervalHours} hours, with metadata for {Overrides} models from {Setting}",
            settings.SyncInterval.TotalHours,
            settings.Overrides.Count,
            CatalogSettings.OverridesKey);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
