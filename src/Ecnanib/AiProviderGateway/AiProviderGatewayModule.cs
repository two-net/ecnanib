using Ecnanib.AiProviderGateway.Adapters;
using Ecnanib.AiProviderGateway.Adapters.Claude;
using Ecnanib.AiProviderGateway.Adapters.Gemini;
using Ecnanib.AiProviderGateway.Adapters.Gpt;
using Ecnanib.AiProviderGateway.Adapters.Grok;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// AI Provider Gateway: one adapter per provider behind a common interface (SRS 3.1). See
/// docs/ai-providers.md.
/// </summary>
public static class AiProviderGatewayModule
{
    /// <summary>
    /// The name of the client of the host's factory that every call to an AI provider is made
    /// with. A test puts a handler of its own behind it, in place of the network.
    /// </summary>
    public const string HttpClientName = "ai-providers";

    /// <summary>
    /// Registers the gateway. Each provider adapter is added here as one more
    /// <see cref="IAiProviderAdapter"/> registration, the check of its keys as one more
    /// <see cref="IAiKeyChecker"/>, and the list of its models as one more
    /// <see cref="IAiModelLister"/>, and nothing outside this component changes.
    /// </summary>
    public static IServiceCollection AddAiProviderGateway(this IServiceCollection services)
    {
        services.AddSingleton<IAiProviderGateway, AiProviderRouter>();
        services.AddGatewayHttpClient();

        // The calls to a model: one adapter for each provider, and the exchange they all make.
        services.AddSingleton<ModelCall>();
        services.AddSingleton<IAiProviderAdapter, ClaudeAdapter>();
        services.AddSingleton<IAiProviderAdapter, GeminiAdapter>();
        services.AddSingleton<IAiProviderAdapter, GptAdapter>();
        services.AddSingleton<IAiProviderAdapter, GrokAdapter>();

        // What pauses the calls to a provider, or the calls with a key, after failures in a row,
        // and where they are paused now (NFR-REL-4).
        services.AddSingleton<AiCallBreakers>();
        services.AddSingleton<IAiCallPauses>(provider => provider.GetRequiredService<AiCallBreakers>());

        // Whether a provider takes a key: a call that only reads, one for each provider (FR-KEY-3).
        services.AddSingleton<IAiKeyChecks, AiKeyCheckRouter>();
        services.AddSingleton<KeyCheckCall>();
        services.AddSingleton<IAiKeyChecker, ClaudeKeyCheck>();
        services.AddSingleton<IAiKeyChecker, GeminiKeyCheck>();
        services.AddSingleton<IAiKeyChecker, GptKeyCheck>();
        services.AddSingleton<IAiKeyChecker, GrokKeyCheck>();

        // The list of a provider's models, which the catalog is filled from: one for each
        // provider, and the exchange they all make (FR-CAT-1).
        services.AddSingleton<IAiModelLists, AiModelListRouter>();
        services.AddSingleton<ModelListCall>();
        services.AddSingleton<IAiModelLister, ClaudeModelList>();
        services.AddSingleton<IAiModelLister, GeminiModelList>();
        services.AddSingleton<IAiModelLister, GptModelList>();
        services.AddSingleton<IAiModelLister, GrokModelList>();

        return services;
    }
}
