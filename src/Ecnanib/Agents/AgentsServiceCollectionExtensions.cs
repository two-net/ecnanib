using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Agents.Services;
using Ecnanib.Agents.Services.Paper;
using Ecnanib.Agents.Services.Placeholders;
using Ecnanib.Keys.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Agents;

public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agents module: validated <see cref="AgentsOptions"/>, <see cref="RiskCapsOptions"/> and
    /// <see cref="PaperOptions"/>; <see cref="IAgentService"/>, <see cref="IStrategyCatalog"/>, <see cref="IAgentEvents"/>
    /// and the runtime's <see cref="IKeyDependentAgents"/>; the <see cref="AgentScheduler"/> and
    /// <see cref="PaperFillSimulator"/> hosted services; and the pipeline seams (<see cref="IMarketDataLoader"/>,
    /// <see cref="IStrategyEngine"/>, <see cref="ICouncil"/>, <see cref="IExecutionService"/>, the live
    /// <see cref="IOrderVenue"/>) with their fail-safe defaults, which an implementation registered earlier replaces.
    /// Requires <c>AddDatabase</c>, <c>AddExchange</c>, <c>AddAccounts</c> and <c>AddKeys</c> (the latter after this
    /// call, so the runtime's key handling is kept). Connects to nothing at registration.
    /// </summary>
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AgentsOptions>()
            .Bind(configuration.GetSection(AgentsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AgentsOptions>, AgentsOptionsValidator>());
        services.AddOptions<RiskCapsOptions>()
            .Bind(configuration.GetSection(RiskCapsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RiskCapsOptions>, RiskCapsOptionsValidator>());
        services.AddOptions<PaperOptions>()
            .Bind(configuration.GetSection(PaperOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PaperOptions>, PaperOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IStrategyCatalog, StrategyCatalog>();
        services.TryAddSingleton<IAgentEvents, AgentEvents>();
        services.TryAddSingleton<CandleStore>();

        // Pipeline seams: the first registration wins, so a module registered before this call provides the real stage.
        services.TryAddSingleton<IMarketDataLoader, RecentCandleLoader>();
        services.TryAddSingleton<IStrategyEngine, PendingStrategyEngine>();
        services.TryAddSingleton<ICouncil, PendingCouncil>();
        services.TryAddSingleton<IExecutionService, PendingExecutionService>();

        services.TryAddSingleton<PaperFillSimulator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PaperFillSimulator>(sp => sp.GetRequiredService<PaperFillSimulator>()));
        services.TryAddSingleton<PaperOrderVenue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOrderVenue, PaperOrderVenue>(sp => sp.GetRequiredService<PaperOrderVenue>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOrderVenue, PendingLiveOrderVenue>());
        services.TryAddSingleton<IOrderVenues, OrderVenues>();

        services.TryAddSingleton<EvaluationPipeline>();
        services.TryAddSingleton<AgentScheduler>();
        services.TryAddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<AgentScheduler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentScheduler>(sp => sp.GetRequiredService<AgentScheduler>()));

        services.TryAddSingleton<IAgentService, AgentService>();
        services.TryAddSingleton<IKeyDependentAgents, AgentKeyDependency>();

        return services;
    }
}
