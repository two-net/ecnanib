namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Base exception for exchange failures. Messages contain the exchange's error text and code only: never a request
/// URL, header, signature or key (FR-LOG-05).
/// </summary>
public class ExchangeException : Exception
{
    public ExchangeException(
        string message,
        ExchangeErrorKind kind = ExchangeErrorKind.Unknown,
        int? exchangeCode = null,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ExchangeCode = exchangeCode;
        IsTransient = isTransient;
    }

    public ExchangeErrorKind Kind { get; }

    public int? ExchangeCode { get; }

    /// <summary>True when a retry of the same request may succeed (network, timeout, rate limit, 5xx).</summary>
    public bool IsTransient { get; }
}

/// <summary>HTTP 429 / 418, code -1003, or the client-side rate-limit gate (EX-05).</summary>
public sealed class ExchangeRateLimitedException : ExchangeException
{
    public ExchangeRateLimitedException(
        string message,
        TimeSpan? retryAfter,
        DateTimeOffset? retryAt,
        bool isIpBan,
        int? exchangeCode = null,
        Exception? innerException = null)
        : base(message, ExchangeErrorKind.RateLimited, exchangeCode, isTransient: true, innerException)
    {
        RetryAfter = retryAfter;
        RetryAt = retryAt;
        IsIpBan = isIpBan;
    }

    public TimeSpan? RetryAfter { get; }

    public DateTimeOffset? RetryAt { get; }

    /// <summary>HTTP 418: the IP is banned; bans scale from 2 minutes to 3 days.</summary>
    public bool IsIpBan { get; }
}

/// <summary>Invalid key, bad signature, missing permission or IP restriction.</summary>
public sealed class ExchangeAuthException : ExchangeException
{
    public ExchangeAuthException(string message, int? exchangeCode = null, Exception? innerException = null)
        : base(message, ExchangeErrorKind.Unauthorized, exchangeCode, isTransient: false, innerException)
    {
    }
}

/// <summary>Network error, timeout or exchange-side 5xx. Always transient.</summary>
public sealed class ExchangeUnavailableException : ExchangeException
{
    public ExchangeUnavailableException(
        string message,
        ExchangeErrorKind kind = ExchangeErrorKind.Network,
        int? exchangeCode = null,
        Exception? innerException = null)
        : base(message, kind, exchangeCode, isTransient: true, innerException)
    {
    }
}

/// <summary>The exchange rejected the request (bad parameters, unknown order, insufficient balance, ...).</summary>
public sealed class ExchangeRequestException : ExchangeException
{
    public ExchangeRequestException(
        string message,
        ExchangeErrorKind kind = ExchangeErrorKind.Unknown,
        int? exchangeCode = null,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, kind, exchangeCode, isTransient, innerException)
    {
    }
}
