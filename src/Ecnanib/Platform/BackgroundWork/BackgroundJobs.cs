using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Platform.BackgroundWork;

/// <summary>Registers scheduled work. See docs/background-work.md.</summary>
public static class BackgroundJobs
{
    /// <summary>
    /// The key of the PostgreSQL advisory lock that the instance which runs the background work
    /// holds for as long as it does (NFR-ARC-4). It is the text "EcnanibW" as a number, next to
    /// the "Ecnanib" of the migration lock, and it shows in <c>pg_locks</c>.
    /// </summary>
    public const long LockKey = 0x45636E616E696257;

    /// <summary>
    /// Adds a job to the work the platform runs on a schedule. A component calls this in its
    /// <c>Add&lt;Name&gt;()</c>. The job is created once, so it takes only what lives as long as
    /// the application does.
    /// </summary>
    public static IServiceCollection AddBackgroundJob<TJob>(this IServiceCollection services)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBackgroundJob, TJob>());

        return services;
    }

    /// <summary>
    /// Registers the scheduler that runs every job. It starts with the host, so after the
    /// database has been migrated.
    /// </summary>
    internal static IServiceCollection AddBackgroundWork(this IServiceCollection services)
    {
        // The clock is a service so that a test can put another one in its place.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<BackgroundJobStore>();
        services.AddSingleton<BackgroundWorkLock>();
        services.AddHostedService<BackgroundJobScheduler>();

        return services;
    }
}
