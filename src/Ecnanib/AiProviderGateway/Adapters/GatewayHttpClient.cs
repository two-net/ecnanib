namespace Ecnanib.AiProviderGateway.Adapters;

/// <summary>The client of the host's factory that every call to an AI provider is made with.</summary>
internal static class GatewayHttpClient
{
    /// <summary>
    /// Registers the client without a time limit of its own. A client of the factory gives up
    /// after 100 seconds unless told otherwise, and a provider's <c>TimeoutSeconds</c> of the
    /// configuration file may be longer. Each call sets its own limit: a call to a model the one
    /// of that file, and the check of a key a shorter one.
    /// </summary>
    public static IServiceCollection AddGatewayHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient(AiProviderGatewayModule.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

        return services;
    }
}
