using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Providers;
using Ecnanib.Ai.Resilience;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI provider module: validated <see cref="AiOptions"/>, one client per provider and the factory.
    /// Nothing contacts a provider until a client is used, and every call carries the user's own key (AI-03).
    /// </summary>
    public static IServiceCollection AddAiProviders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AiOptions>, AiOptionsValidator>());

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(AiProviderClientBase.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = sp.GetRequiredService<IOptions<AiOptions>>().Value.CallTimeout)
            // FR-LOG-05: the factory's logging handler would otherwise print auth headers at Trace.
            .RedactLoggedHeaders(["x-api-key", "Authorization", "x-goog-api-key"]);

        services.TryAddSingleton<AiRetryPolicy>();
        services.TryAddSingleton<ClaudeClient>();
        services.TryAddSingleton<GeminiClient>();
        services.TryAddSingleton<GptClient>();
        services.TryAddSingleton<GrokClient>();
        services.TryAddSingleton<IAiClientFactory, AiClientFactory>();

        return services;
    }
}
