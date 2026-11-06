using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.DecisionCycle;

public static class DecisionCycleModule
{
    public static IServiceCollection AddDecisionCycle(this IServiceCollection services)
    {
        services.AddSingleton(provider => DecisionCycleSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddHostedService<CycleRecovery>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CycleObservers>();
        services.AddSingleton<IDecisionCycles, DecisionCycles>();
        services.AddSingleton<IDecisionTraces, DecisionTraces>();
        services.AddSingleton<IAgentHistory, AgentCycleHistory>();

        return services;
    }
}
