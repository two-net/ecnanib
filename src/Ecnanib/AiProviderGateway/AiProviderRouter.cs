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
        // Said without what was given: a prompt and a key are in no error.
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResultSchema, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (!_adapters.TryGetValue(provider, out var adapter))
        {
            throw new InvalidOperationException($"No adapter is registered for AI provider '{provider}'.");
        }

        // Everything logged during the call, the adapter's HTTP calls included, names the provider
        // and the model. The key is never given to the logger.
        using var call = _logger.BeginCorrelation(new Correlation(Provider: provider.Value, Model: request.Model));
        var started = Stopwatch.GetTimestamp();

        AiCallResult result;
        try
        {
            result = await adapter.CompleteAsync(request, apiKey, cancellationToken);
        }
        catch (Exception error)
        {
            // A caller that cancelled, or something that an adapter did not expect. Logged here
            // for its duration and outcome. What it means is the caller's to decide.
            _logger.LogWarning(
                error,
                "AI call {Outcome} in {DurationMs} ms",
                error is OperationCanceledException ? "Canceled" : "Failed",
                Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));

            throw;
        }

        Log(result);

        return result;
    }

    /// <summary>
    /// Logs a call by how it ended: one that gave no result is a warning. The duration is the
    /// latency of the result, which is what the caller records, and the tokens are there when the
    /// provider counted them.
    /// </summary>
    private void Log(AiCallResult result)
    {
        var level = result.Outcome == AiCallOutcome.Succeeded ? LogLevel.Information : LogLevel.Warning;
        var outcome = result.Outcome.ToString();
        var duration = Math.Round(result.Latency.TotalMilliseconds, 1);

        if (result.Usage is { } usage)
        {
            _logger.Log(
                level,
                "AI call {Outcome} in {DurationMs} ms, using {InputTokens} input and {OutputTokens} output tokens",
                outcome,
                duration,
                usage.InputTokens,
                usage.OutputTokens);
        }
        else
        {
            _logger.Log(level, "AI call {Outcome} in {DurationMs} ms", outcome, duration);
        }
    }
}
