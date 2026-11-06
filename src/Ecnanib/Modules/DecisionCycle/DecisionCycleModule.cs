using Ecnanib.Modules.DecisionCycle.Contracts;

namespace Ecnanib.Modules.DecisionCycle;

public static class DecisionCycleModule
{
    public static IServiceCollection AddDecisionCycle(this IServiceCollection services)
    {
        services.AddSingleton<IDecisionCycles, DecisionCycles>();

        return services;
    }
}
