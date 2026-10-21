using Ecnanib.Agents.Abstractions;
using Ecnanib.Council.Abstractions;
using Ecnanib.Experience.Abstractions;
using Ecnanib.Experience.Configuration;
using Ecnanib.Experience.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Experience;

public static class ExperienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the experience module: validated <see cref="ExperienceOptions"/>; the <see cref="IExperienceSummaries"/>
    /// the council's prompts carry (FR-EXP-06), the pipeline's <see cref="IExperienceTracker"/> (FR-EXP-03) and the
    /// <see cref="ICouncilSuggestions"/> agent creation uses (FR-EXP-08), which replace the council's and the agents
    /// module's defaults; the <see cref="IExperienceReader"/> of the pages (FR-EXP-07); and the
    /// <see cref="ExperienceRefresher"/> hosted service (FR-EXP-04, FR-EXP-05). Register it <b>before</b>
    /// <c>AddCouncil</c> and <c>AddAgents</c>, whose placeholders would otherwise stay. Requires <c>AddDatabase</c>.
    /// Connects to nothing at registration.
    /// </summary>
    public static IServiceCollection AddExperience(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ExperienceOptions>()
            .Bind(configuration.GetSection(ExperienceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ExperienceOptions>, ExperienceOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IExperienceSummaries, ExperienceSummaries>();
        services.TryAddSingleton<IExperienceTracker, ExperienceTracker>();
        services.TryAddSingleton<ICouncilSuggestions, CouncilSuggestions>();
        services.TryAddSingleton<IExperienceReader, ExperienceReader>();

        services.TryAddSingleton<ExperienceRefresher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExperienceRefresher>(sp => sp.GetRequiredService<ExperienceRefresher>()));
        return services;
    }
}
