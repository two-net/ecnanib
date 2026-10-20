using Ecnanib.Agents.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Configuration;
using Ecnanib.Council.Services;
using Ecnanib.Council.Services.Placeholders;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Council;

public static class CouncilServiceCollectionExtensions
{
    /// <summary>
    /// Registers the council module: validated <see cref="CouncilOptions"/>, the <see cref="ICouncil"/> of Appendix B
    /// (FR-AI), the default <see cref="IExperienceSummaries"/> (register the experience module's first to replace it)
    /// and <see cref="IDeliberationReader"/> (FR-AI-14). Register it <b>before</b> <c>AddAgents</c>, whose fail-safe
    /// council would otherwise stay. Requires <c>AddDatabase</c>, <c>AddAiProviders</c> and <c>AddKeys</c> (both
    /// resolved at evaluation time) and the agents module's risk caps. Connects to nothing at registration.
    /// </summary>
    public static IServiceCollection AddCouncil(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CouncilOptions>()
            .Bind(configuration.GetSection(CouncilOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CouncilOptions>, CouncilOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IExperienceSummaries, NoExperienceSummaries>();
        services.TryAddSingleton<ICouncil, Services.Council>();
        services.TryAddSingleton<IDeliberationReader, DeliberationReader>();
        return services;
    }
}
