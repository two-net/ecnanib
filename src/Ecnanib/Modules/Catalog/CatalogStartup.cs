namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Says at startup how often the catalog syncs, how many models the configuration describes, and the roles'
/// default providers. The host builds every hosted service before it starts any, and this one takes
/// <see cref="CatalogSettings"/> and <see cref="RoleDefaultSettings"/>, so an invalid setting stops startup
/// before any migration runs (CFG-03).
/// </summary>
internal sealed class CatalogStartup(CatalogSettings settings, RoleDefaultSettings roleDefaults, ILogger<CatalogStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "The model catalog syncs every {SyncIntervalHours} hours, retires a model {RetireAfterDays} days after no key lists it, "
            + "and has metadata for {Overrides} models from {Setting}",
            settings.SyncInterval.TotalHours,
            settings.RetireAfter.TotalDays,
            settings.Overrides.Count,
            CatalogSettings.OverridesKey);

        // Only a role with no row yet takes these (NFR-DEP-02), so the table may differ, and that's not a fault.
        logger.LogInformation(
            "{Setting} gives {Roles} roles a default, for any role that has none yet: {Defaults}",
            RoleDefaultSettings.Section,
            roleDefaults.Defaults.Count,
            string.Join(", ", roleDefaults.Defaults.Select(value => $"{value.Role} {value.Provider} {value.ModelId}".TrimEnd())));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
