namespace Ecnanib.Modules.AiAdapters;

/// <summary>
/// Says at startup which AI providers are configured. The host builds every hosted service before it
/// starts any, and this one takes <see cref="AiProviderSettings"/>, so an invalid setting stops startup
/// before any migration runs (CFG-03). It logs under its own name, because the AiCouncil category is for
/// AI calls, each under the correlation ID of the work that made it.
/// </summary>
internal sealed class AiAdaptersStartup(AiProviderSettings settings, ILogger<AiAdaptersStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Providers.Count == 0)
        {
            logger.LogInformation("No AI provider adapters are registered");
        }

        foreach (var provider in settings.Providers)
        {
            if (provider.Enabled)
            {
                logger.LogInformation(
                    "AI provider {Provider}: {Host}, timeout {TimeoutSeconds} s, {MaxRetries} retries from {RetryBackoffMs} ms, "
                    + "{MaxConcurrentPerCredential} concurrent calls per credential",
                    provider.Name,
                    provider.BaseUrl!.Host,
                    provider.Timeout.TotalSeconds,
                    provider.MaxRetries,
                    provider.RetryBackoff.TotalMilliseconds,
                    provider.MaxConcurrentPerCredential);
            }
            else
            {
                logger.LogInformation("AI provider {Provider} is disabled, so calls to it fail without being sent", provider.Name);
            }
        }

        foreach (var section in settings.Unmatched)
        {
            logger.LogWarning("{Section}:{Provider} is ignored, because no AI provider adapter has that name", AiProviderSettings.Section, section);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
