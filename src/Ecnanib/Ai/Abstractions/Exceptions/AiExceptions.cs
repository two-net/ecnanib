namespace Ecnanib.Ai.Abstractions;

/// <summary>
/// Base exception for provider failures. Messages carry the provider's error text (with any key value scrubbed) and
/// code only: never a URL, header, request body, prompt or key (FR-LOG-05). <see cref="Model"/> is set on every
/// failure of a completion so a rejected model never has to be parsed out of the message (FR-SYNC-04).
/// <see cref="RetryAfter"/> is the provider's wait hint, honoured by the retry policy for any transient failure.
/// </summary>
public class AiException : Exception
{
    public AiException(
        string message,
        AiProvider provider,
        AiErrorKind kind = AiErrorKind.Unknown,
        int? statusCode = null,
        string? providerCode = null,
        bool isTransient = false,
        string? model = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        DateTimeOffset? retryAt = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Provider = provider;
        Kind = kind;
        StatusCode = statusCode;
        ProviderCode = providerCode;
        IsTransient = isTransient;
        Model = model;
        RequestId = requestId;
        RetryAfter = retryAfter;
        RetryAt = retryAt;
    }

    public AiProvider Provider { get; }

    public AiErrorKind Kind { get; }

    /// <summary>The HTTP status, when the provider answered.</summary>
    public int? StatusCode { get; }

    /// <summary>The provider's own error code or type, for example <c>invalid_api_key</c> or <c>RESOURCE_EXHAUSTED</c>.</summary>
    public string? ProviderCode { get; }

    /// <summary>True when a retry of the same request may succeed (network, timeout, rate limit, overload, 5xx).</summary>
    public bool IsTransient { get; }

    public string? Model { get; }

    /// <summary>The provider's request identifier header, safe to log and useful for support.</summary>
    public string? RequestId { get; }

    /// <summary>How long the provider asked us to wait (Retry-After header, RetryInfo or message text), if it said.</summary>
    public TimeSpan? RetryAfter { get; }

    public DateTimeOffset? RetryAt { get; }
}

/// <summary>HTTP 429: the provider is throttling this key. <see cref="AiException.RetryAfter"/> is honoured by the retry policy.</summary>
public sealed class AiRateLimitedException : AiException
{
    public AiRateLimitedException(
        string message,
        AiProvider provider,
        TimeSpan? retryAfter,
        DateTimeOffset? retryAt,
        int? statusCode = null,
        string? providerCode = null,
        string? model = null,
        string? requestId = null,
        Exception? innerException = null)
        : base(message, provider, AiErrorKind.RateLimited, statusCode, providerCode, isTransient: true, model, requestId, retryAfter, retryAt, innerException)
    {
    }
}

/// <summary>Invalid, revoked or insufficiently permitted key (FR-KEY-03, FR-KEY-07). Never transient.</summary>
public sealed class AiAuthException : AiException
{
    public AiAuthException(
        string message,
        AiProvider provider,
        int? statusCode = null,
        string? providerCode = null,
        string? model = null,
        string? requestId = null,
        Exception? innerException = null)
        : base(message, provider, AiErrorKind.Unauthorized, statusCode, providerCode, isTransient: false, model, requestId, null, null, innerException)
    {
    }
}

/// <summary>Network error, timeout, overload or provider-side 5xx. Always transient.</summary>
public sealed class AiUnavailableException : AiException
{
    public AiUnavailableException(
        string message,
        AiProvider provider,
        AiErrorKind kind = AiErrorKind.Network,
        int? statusCode = null,
        string? providerCode = null,
        string? model = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        DateTimeOffset? retryAt = null,
        Exception? innerException = null)
        : base(message, provider, kind, statusCode, providerCode, isTransient: true, model, requestId, retryAfter, retryAt, innerException)
    {
    }
}

/// <summary>The provider rejected the request, or answered with something the module cannot read. Never transient.</summary>
public sealed class AiRequestException : AiException
{
    public AiRequestException(
        string message,
        AiProvider provider,
        AiErrorKind kind = AiErrorKind.InvalidRequest,
        int? statusCode = null,
        string? providerCode = null,
        string? model = null,
        string? requestId = null,
        Exception? innerException = null)
        : base(message, provider, kind, statusCode, providerCode, isTransient: false, model, requestId, null, null, innerException)
    {
    }
}
