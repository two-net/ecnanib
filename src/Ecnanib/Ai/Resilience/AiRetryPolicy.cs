using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Resilience;

/// <summary>
/// Retries transient provider failures (network, timeout, rate limit, overload, 5xx) with jittered exponential
/// backoff within the configured bounds, honouring Retry-After (FR-AI-13). Both module operations are idempotent
/// reads, so every call runs under this policy. The caller's token is the deadline: it interrupts the request and
/// the delay alike. A retried client-side timeout can bill twice if the provider finished the first attempt; that is
/// accepted and bounded by <see cref="AiRetryOptions.MaxAttempts"/>.
/// </summary>
internal sealed class AiRetryPolicy
{
    private readonly IOptions<AiOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AiRetryPolicy> _logger;
    private readonly Func<double> _jitter;

    public AiRetryPolicy(IOptions<AiOptions> options, TimeProvider time, ILogger<AiRetryPolicy> logger)
        : this(options, time, logger, static () => 0.75 + Random.Shared.NextDouble() * 0.5)
    {
    }

    internal AiRetryPolicy(IOptions<AiOptions> options, TimeProvider time, ILogger<AiRetryPolicy> logger, Func<double> jitter)
    {
        _options = options;
        _time = time;
        _logger = logger;
        _jitter = jitter;
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, string operationName, CancellationToken ct)
    {
        var retry = _options.Value.Retry;
        var maxAttempts = Math.Max(1, retry.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (AiException ex) when (ex.IsTransient && attempt < maxAttempts)
            {
                var delay = ComputeDelay(attempt, retry);
                if (ex.RetryAfter is { } retryAfter)
                {
                    if (retryAfter > retry.MaxRetryAfterWait)
                    {
                        _logger.LogWarning(
                            "AI {Provider} {Operation} asked for a {RetryAfter} wait, longer than the configured maximum; giving up",
                            ex.Provider, operationName, retryAfter);
                        throw;
                    }

                    if (retryAfter > delay)
                        delay = retryAfter;
                }

                _logger.LogWarning(
                    ex,
                    "AI {Provider} {Operation} failed with {Kind} (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                    ex.Provider, operationName, ex.Kind, attempt, maxAttempts, delay);
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt, AiRetryOptions retry)
    {
        var exponential = retry.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, retry.MaxDelay.TotalMilliseconds);
        var jittered = capped * Math.Clamp(_jitter(), 0.5, 1.5);
        return TimeSpan.FromMilliseconds(Math.Max(1, jittered));
    }
}
