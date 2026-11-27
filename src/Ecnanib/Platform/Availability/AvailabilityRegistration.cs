using Ecnanib.Platform.BackgroundWork;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Platform.Availability;

/// <summary>Registers the measure of availability and the maintenance that is announced.</summary>
internal static class AvailabilityRegistration
{
    /// <summary>
    /// Registers what says each minute that the service is up, what reads its availability from
    /// that, and what reads the maintenance that is announced (NFR-REL-1, NFR-REL-6). See
    /// docs/reliability.md.
    /// </summary>
    public static IServiceCollection AddAvailability(this IServiceCollection services)
    {
        // The clock is a service so that a test can put another one in its place.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<Uptime>();
        services.AddBackgroundJob<UptimeJob>();

        // How available the service was, for the operator status screen.
        services.AddSingleton<IAvailability, AvailabilityMeasure>();

        // What is announced, for the banner of every page and for the page of an operator.
        services.AddSingleton<IAnnouncedMaintenance, AnnouncedMaintenances>();

        return services;
    }
}
