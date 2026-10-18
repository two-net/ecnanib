using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange.Resilience;

/// <summary>Which calls may be retried. Order placement and cancellation are never retried (FR-EXE-07, NFR-REL-02).</summary>
internal enum RetryProfile
{
    None = 0,
    /// <summary>Reads and idempotent settings: bounded, jittered exponential backoff that honours Retry-After.</summary>
    Query = 1,
}

/// <summary>Retries transient exchange failures (network, timeout, rate limit) within the configured bounds (EX-05).</summary>
internal sealed class ExchangeRetryPolicy
{
    private readonly IOptions<ExchangeOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExchangeRetryPolicy> _logger;
    private readonly Func<double> _jitter;

    public ExchangeRetryPolicy(IOptions<ExchangeOptions> options, TimeProvider time, ILogger<ExchangeRetryPolicy> logger)
        : this(options, time, logger, static () => 0.75 + Random.Shared.NextDouble() * 0.5)
    {
    }

    internal ExchangeRetryPolicy(IOptions<ExchangeOptions> options, TimeProvider time, ILogger<ExchangeRetryPolicy> logger, Func<double> jitter)
    {
        _options = options;
        _time = time;
        _logger = logger;
        _jitter = jitter;
    }

    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, RetryProfile profile, string operationName, CancellationToken ct)
    {
        await ExecuteAsync<object?>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        }, profile, operationName, ct).ConfigureAwait(false);
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryProfile profile, string operationName, CancellationToken ct)
    {
        var retry = _options.Value.Retry;
        var maxAttempts = profile == RetryProfile.None ? 1 : Math.Max(1, retry.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (ExchangeException ex) when (ex.IsTransient && attempt < maxAttempts)
            {
                var delay = ComputeDelay(attempt, retry);
                if (ex is ExchangeRateLimitedException limited && limited.RetryAfter is { } retryAfter)
                {
                    if (retryAfter > retry.MaxRetryAfterWait)
                    {
                        _logger.LogWarning(
                            "Exchange {Operation} is rate limited for {RetryAfter}, longer than the configured maximum wait; giving up",
                            operationName, retryAfter);
                        throw;
                    }

                    if (retryAfter > delay)
                        delay = retryAfter;
                }

                _logger.LogWarning(
                    ex,
                    "Exchange {Operation} failed with {Kind} (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                    operationName, ex.Kind, attempt, maxAttempts, delay);
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt, ExchangeRetryOptions retry)
    {
        var exponential = retry.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, retry.MaxDelay.TotalMilliseconds);
        var jittered = capped * Math.Clamp(_jitter(), 0.5, 1.5);
        return TimeSpan.FromMilliseconds(Math.Max(1, jittered));
    }
}
