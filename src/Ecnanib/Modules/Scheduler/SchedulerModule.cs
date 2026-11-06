using Ecnanib.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.Scheduler;

public static class SchedulerModule
{
    public static IServiceCollection AddScheduler(this IServiceCollection services)
    {
        services.AddSingleton(provider => SchedulerSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddHostedService<SchedulerStartup>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CycleSlots>();
        services.AddSingleton<CycleDispatcher>();
        services.AddSingleton<CycleScheduler>();
        services.AddSingleton<IRecurringJob, FifteenMinuteCycleJob>();
        services.AddSingleton<IRecurringJob, HourlyCycleJob>();
        services.AddSingleton<IRecurringJob, FourHourCycleJob>();
        services.AddSingleton<IRecurringJob, DailyCycleJob>();

        return services;
    }
}
