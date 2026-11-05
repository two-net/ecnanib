using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.Execution;

public static class ExecutionModule
{
    public static IServiceCollection AddExecution(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IExchangeBanObserver, ExchangeBanNotifier>();
        services.AddSingleton<IAgentExposure, AgentExposure>();

        return services;
    }
}
