using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.Agents;

public static class AgentsModule
{
    public static IServiceCollection AddAgents(this IServiceCollection services)
    {
        services.AddSingleton(provider => AgentsSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddHostedService<AgentsStartup>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ActivationChecks>();
        services.AddSingleton<AgentPrerequisites>();
        services.AddSingleton<IAgents, Agents>();
        services.AddSingleton<ICredentialObserver, AgentCredentialObserver>();
        services.AddSingleton<IRecurringJob, AgentPrerequisitesJob>();

        return services;
    }
}
