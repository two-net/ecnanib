namespace Ecnanib.AiProviderGateway;

/// <summary>AI Provider Gateway: one adapter per provider behind a common interface (SRS 3.1).</summary>
public static class AiProviderGatewayModule
{
    /// <summary>
    /// Registers the gateway. Each provider adapter is added here as one more
    /// <see cref="IAiProviderAdapter"/> registration, and nothing outside this component changes.
    /// </summary>
    public static IServiceCollection AddAiProviderGateway(this IServiceCollection services)
    {
        services.AddSingleton<IAiProviderGateway, AiProviderRouter>();

        return services;
    }
}
