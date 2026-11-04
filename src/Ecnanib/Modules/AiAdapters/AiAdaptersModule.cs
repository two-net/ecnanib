using System.Net;
using Ecnanib.Modules.AiAdapters.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.AiAdapters;

public static class AiAdaptersModule
{
    /// <summary>
    /// The largest answer kept in memory. Far more than any model list or response, so only a server gone
    /// wrong reaches it.
    /// </summary>
    private const long MaximumResponseBytes = 32 * 1024 * 1024;

    public static IServiceCollection AddAiAdapters(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => AiProviderSettings.Read(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IHostEnvironment>(),
            provider.GetServices<IAiProviderAdapter>()));
        services.AddHostedService<AiAdaptersStartup>();

        // No redirects, or a response could send a user's key somewhere the configuration never named
        // (NFR-SEC-10). No logging of its own either, because the factory's handlers would log each URI.
        // Timeouts are per attempt, in AiTransport.
        services.AddHttpClient(AiTransport.ClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = MaximumResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseCookies = false,
            })
            .RemoveAllLoggers();

        services.AddSingleton<CredentialGates>();
        services.AddSingleton<AiTransport>();
        services.AddSingleton<AiGateway>();
        services.AddSingleton<IAiProviders>(provider => provider.GetRequiredService<AiGateway>());
        services.AddSingleton<IAiCompletions>(provider => provider.GetRequiredService<AiGateway>());

        // CON-13. Each needs its AiProviders section. They take nothing from the container, so a key can
        // only arrive with a call, never from the configuration.
        services.AddSingleton<IAiProviderAdapter, AnthropicAdapter>();
        services.AddSingleton<IAiProviderAdapter, GoogleAdapter>();
        services.AddSingleton<IAiProviderAdapter, OpenAiAdapter>();
        services.AddSingleton<IAiProviderAdapter, XaiAdapter>();

        return services;
    }
}
