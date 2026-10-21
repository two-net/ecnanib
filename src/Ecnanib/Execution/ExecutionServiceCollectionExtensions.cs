using Ecnanib.Agents.Abstractions;
using Ecnanib.Execution.Configuration;
using Ecnanib.Execution.Services;
using Ecnanib.Execution.Services.Live;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution;

public static class ExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the execution module: validated <see cref="ExecutionOptions"/>; the <see cref="IExecutionService"/>
    /// that sizes, enforces the venue-bound limits and manages positions at candle close (FR-EXE-03..06, FR-EXE-09);
    /// the live <see cref="IOrderVenue"/> on Binance (FR-EXE-01, FR-EXE-02, FR-EXE-07, FR-EXE-10..12); and two hosted
    /// services, the <see cref="LiveFillTracker"/> on the user-data streams and the <see cref="ExchangeReconciler"/>
    /// (FR-EXE-08). Both replace the agents module's fail-safe defaults, so register it <b>before</b> <c>AddAgents</c>;
    /// it throws when <c>AddAgents</c> came first. Requires <c>AddExchange</c>, <c>AddDatabase</c> and, at call time,
    /// the keys module's resolver. Connects to nothing at registration.
    /// </summary>
    public static IServiceCollection AddExecution(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(d => d.ServiceType == typeof(IExecutionService)))
            throw new InvalidOperationException("AddExecution must be registered before AddAgents, whose fail-safe execution would otherwise stay.");

        services.AddOptions<ExecutionOptions>()
            .Bind(configuration.GetSection(ExecutionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ExecutionOptions>, ExecutionOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<LiveExchange>();
        services.TryAddSingleton<FeeConverter>();
        services.TryAddSingleton<FuturesAccountSetup>();
        services.TryAddSingleton<OrderRows>();
        services.TryAddSingleton<SpotOrders>();
        services.TryAddSingleton<FuturesOrders>();
        services.TryAddSingleton<ProtectionPlacer>();
        services.TryAddSingleton<TradeLedger>();
        services.TryAddSingleton<FillHandler>();
        services.TryAddSingleton<ExchangeReconciler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExchangeReconciler>(sp => sp.GetRequiredService<ExchangeReconciler>()));
        services.TryAddSingleton<LiveFillTracker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LiveFillTracker>(sp => sp.GetRequiredService<LiveFillTracker>()));
        services.TryAddSingleton<LiveOrderVenue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOrderVenue, LiveOrderVenue>(sp => sp.GetRequiredService<LiveOrderVenue>()));
        services.TryAddSingleton<IExecutionService, ExecutionService>();
        return services;
    }
}
