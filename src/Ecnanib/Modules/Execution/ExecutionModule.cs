using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.Execution;

public static class ExecutionModule
{
    public static IServiceCollection AddExecution(this IServiceCollection services)
    {
        services.AddSingleton<IExchangeBanObserver, ExchangeBanNotifier>();

        return services;
    }
}
