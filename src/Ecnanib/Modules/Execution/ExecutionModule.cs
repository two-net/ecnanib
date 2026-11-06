using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.Execution;

public static class ExecutionModule
{
    public static IServiceCollection AddExecution(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => ExecutionSettings.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IExchangeBanObserver, ExchangeBanNotifier>();
        services.AddSingleton<AgentLocks>();
        services.AddSingleton<AgentSessions>();
        services.AddSingleton<OrderStore>();
        services.AddSingleton<ProtectionKeeper>();
        services.AddSingleton<OrderWatch>();

        // The sweep runs every minute, and once at startup when there's anything to resume.
        services.AddSingleton<IRecurringJob, OrderWatchSweep>();
        services.AddHostedService<OrderWatchSweep>();
        services.AddSingleton<IOrderExecution, OrderExecutor>();
        services.AddSingleton<IAgentHistory, OrderHistory>();
        services.AddSingleton<IAgentExposure, AgentExposure>();
        services.AddSingleton<IAgentAccounts, AgentAccounts>();
        services.AddSingleton<IFuturesSetup, FuturesSetup>();

        return services;
    }
}
