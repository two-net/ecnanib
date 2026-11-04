namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// Why an AI call failed, the same for every provider, for metrics and fallback (EXT-AI-04). Values are
/// stored by name, so one is never renamed.
/// </summary>
public enum AiFailure
{
    /// <summary>The provider refused the key. It's invalid, revoked or lacks access. Not retried.</summary>
    Authentication,

    /// <summary>The key's rate limit or quota. Retried after the provider's Retry-After, or a backoff.</summary>
    RateLimit,

    /// <summary>The provider is overloaded, such as Anthropic's 529. Retried.</summary>
    Overloaded,

    /// <summary>The provider said the request is wrong, such as an unknown model or an oversized prompt. Not retried.</summary>
    InvalidRequest,

    /// <summary>The model declined to answer. Retried, because another attempt may answer (FR-ROLE-06).</summary>
    Refusal,

    /// <summary>No answer within AiProviders:&lt;Name&gt;:TimeoutSeconds, or the provider timed out itself. Retried.</summary>
    Timeout,

    /// <summary>
    /// Anything else. A server error or a broken connection is retried. A redirect, which is never
    /// followed, an unexpected status, an answer this version can't read, or a disabled provider isn't.
    /// </summary>
    Other,
}

/// <summary>
/// An AI call that failed after its retries. The message holds only the provider, the operation, the
/// status and the provider's error code. It never holds a key, a prompt or the provider's own message,
/// which can quote the prompt, because whoever catches this may log it. That message is
/// <see cref="ProviderMessage"/>, for the decision trace only.
/// </summary>
public sealed class AiException(string provider, AiFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Provider { get; } = provider;

    public AiFailure Failure { get; } = failure;

    /// <summary>The last attempt's HTTP status, or null when no answer arrived.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The provider's error type or code, such as "overloaded_error", when it gave a plain one.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// The provider's own explanation, cut short. It can quote the prompt, so it goes in the decision trace
    /// and never in a log (FR-LOG-07).
    /// </summary>
    public string? ProviderMessage { get; init; }

    /// <summary>How long the provider asked to wait, when it said so.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Tokens billed across every attempt, such as for a refusal. Zero when nothing was answered.</summary>
    public AiUsage Usage { get; init; } = AiUsage.Zero;

    /// <summary>What those tokens cost, in US dollars, or null when the request had no price.</summary>
    public decimal? Cost { get; init; }

    /// <summary>How many times it was sent. Zero when it never was.</summary>
    public int Attempts { get; init; }
}
