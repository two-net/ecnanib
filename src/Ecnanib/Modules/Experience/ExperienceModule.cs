using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Experience.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.Experience;

public static class ExperienceModule
{
    public static IServiceCollection AddExperience(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => ExperienceSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddHostedService<ExperienceStartup>();
        services.AddSingleton<IExperienceMetrics, ExperienceMetrics>();
        services.AddSingleton<IRecurringJob, ExperienceMetricsJob>();
        services.AddSingleton<IAgentHistory, ExperienceHistory>();

        return services;
    }
}
