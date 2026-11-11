using System.Diagnostics;
using Ecnanib.Platform.Logging;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// Routes each call to the adapter registered for its provider, and logs it with its duration,
/// outcome, and token usage (NFR-LOG-5).
/// </summary>
internal sealed class AiProviderRouter : IAiProviderGateway
{
    private readonly Dictionary<AiProviderCode, IAiProviderAdapter> _adapters = [];
    private readonly ILogger<AiProviderRouter> _logger;

    public AiProviderRouter(IEnumerable<IAiProviderAdapter> adapters, ILogger<AiProviderRouter> logger)
    {
        _logger = logger;

        foreach (var adapter in adapters)
        {
            if (!_adapters.TryAdd(adapter.Provider, adapter))
            {
                throw new InvalidOperationException(
                    $"More than one adapter is registered for AI provider '{adapter.Provider}'.");
            }
        }
    }

    public IReadOnlyCollection<AiProviderCode> Providers => _adapters.Keys;

    public async Task<AiCallResult> CompleteAsync(
        AiProviderCode provider,
        AiCallRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(provider, out var adapter))
        {
            throw new InvalidOperationException($"No adapter is registered for AI provider '{provider}'.");
        }

        // Everything logged during the call, the adapter's HTTP calls included, names the provider
        // and the model. The key is never given to the logger.
        using var call = _logger.BeginCorrelation(new Correlation(Provider: provider.Value, Model: request.Model));
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await adapter.CompleteAsync(request, apiKey, cancellationToken);

            _logger.LogInformation(
                "AI call {Outcome} in {DurationMs} ms, using {InputTokens} input and {OutputTokens} output tokens",
                "Succeeded",
                Milliseconds(started),
                result.Usage.InputTokens,
                result.Usage.OutputTokens);

            return result;
        }
        catch (Exception error)
        {
            // Logged here for its duration and outcome. What it means is the caller's to decide.
            _logger.LogWarning(
                error,
                "AI call {Outcome} in {DurationMs} ms",
                error is OperationCanceledException ? "Canceled" : "Failed",
                Milliseconds(started));

            throw;
        }
    }

    private static double Milliseconds(long started) =>
        Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
}
