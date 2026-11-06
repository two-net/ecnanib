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
        services.AddSingleton<Reconciler>();
        services.AddSingleton<AgentReconciliation>();
        services.AddSingleton<TradeBook>();
        services.AddSingleton<TradeSettlement>();
        services.AddSingleton<ITradeJournal, TradeJournal>();

        // The watch's sweep and the trade journal's run every minute. Reconciliation runs on its interval, and once at
        // startup, before any scheduled cycle (FR-EXE-08).
        services.AddSingleton<IRecurringJob, OrderWatchSweep>();
        services.AddSingleton<IRecurringJob, ReconcileSweep>();
        services.AddSingleton<IRecurringJob, TradeJournalSweep>();
        services.AddHostedService<ReconcileStartup>();
        services.AddSingleton<IOrderExecution, OrderExecutor>();
        services.AddSingleton<IAgentHistory, OrderHistory>();
        services.AddSingleton<IAgentExposure, AgentExposure>();
        services.AddSingleton<IAgentLiquidation, AgentLiquidation>();
        services.AddSingleton<IAgentAccounts, AgentAccounts>();
        services.AddSingleton<IFuturesSetup, FuturesSetup>();

        return services;
    }
}
