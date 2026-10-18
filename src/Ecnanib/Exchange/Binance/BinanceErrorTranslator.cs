using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Binance;

/// <summary>
/// Turns Binance.Net call results into the module's typed exceptions (EX-05 rate limits, FR-KEY-07 auth failures).
/// Messages carry the exchange's error text and code only: never the request URL, headers or credentials (FR-LOG-05).
/// </summary>
internal static partial class BinanceErrorTranslator
{
    public static T EnsureSuccess<T>(HttpResult<T> result, string operation, MarketType market, DateTimeOffset? now = null)
    {
        if (result.Success)
            return result.Data!;

        throw Translate(result.Error, operation, market, now, result.ResponseStatusCode);
    }

    public static void EnsureSuccess(HttpResult result, string operation, MarketType market, DateTimeOffset? now = null)
    {
        if (!result.Success)
            throw Translate(result.Error, operation, market, now, result.ResponseStatusCode);
    }

    public static T EnsureSuccess<T>(CallResult<T> result, string operation, MarketType market, DateTimeOffset? now = null)
    {
        if (result.Success)
            return result.Data!;

        throw Translate(result.Error, operation, market, now);
    }

    public static T EnsureSuccess<T>(WebSocketResult<T> result, string operation, MarketType market, DateTimeOffset? now = null)
    {
        if (result.Success)
            return result.Data!;

        throw Translate(result.Error, operation, market, now);
    }

    public static void EnsureSuccess(CallResult result, string operation, MarketType market, DateTimeOffset? now = null)
    {
        if (!result.Success)
            throw Translate(result.Error, operation, market, now);
    }

    public static ExchangeException Translate(Error? error, string operation, MarketType market, DateTimeOffset? now = null, HttpStatusCode? statusCode = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var code = error?.Code ?? (statusCode is { } sc ? (int)sc : null);
        var detail = Describe(error);
        var text = $"Binance {market} {operation} failed: {detail}" + (code is { } c ? $" (code {c})" : string.Empty);

        if (error is null)
            return new ExchangeRequestException(text);

        var status = statusCode is { } http ? (int?)http : null;
        if (IsRateLimit(error, code) || status is 429 or 418)
        {
            DateTimeOffset? retryAt = error is BaseRateLimitError { RetryAfter: { } after }
                ? new DateTimeOffset(DateTime.SpecifyKind(after, DateTimeKind.Utc))
                : ParseBannedUntil(detail);
            TimeSpan? retryAfter = retryAt is { } at ? (at > current ? at - current : TimeSpan.Zero) : null;
            var isIpBan = code == 418 || status == 418 || detail.Contains("banned", StringComparison.OrdinalIgnoreCase);
            return new ExchangeRateLimitedException(text, retryAfter, retryAt, isIpBan, code, error.Exception);
        }

        if (error.ErrorType == ErrorType.Unauthorized || error.ErrorType == ErrorType.MissingCredentials ||
            code is -2014 or -2015 or -1022 or -1002 or -2008 or -1099 or 401 or 403 || status is 401 or 403)
        {
            return new ExchangeAuthException(text, code, error.Exception);
        }

        if (IsUnavailable(error, code) || status is >= 500 and <= 599)
        {
            var kind = error.ErrorType switch
            {
                ErrorType.Timeout => ExchangeErrorKind.Timeout,
                ErrorType.SystemError => ExchangeErrorKind.SystemError,
                _ when code is >= 500 and <= 599 || status is >= 500 and <= 599 => ExchangeErrorKind.SystemError,
                _ => ExchangeErrorKind.Network,
            };
            return new ExchangeUnavailableException(text, kind, code, error.Exception);
        }

        return new ExchangeRequestException(text, MapKind(error.ErrorType, code), code, error.IsTransient, error.Exception);
    }

    /// <summary>
    /// The exchange's own text is in <see cref="Error.ErrorDescription"/>; <see cref="Error.Message"/> only carries extra
    /// text for library-side errors. Neither contains the request URL or headers.
    /// </summary>
    private static string Describe(Error? error)
    {
        if (error is null)
            return "unknown error";

        var description = error.ErrorDescription?.Trim();
        var message = error.Message?.TrimStart(':', ' ').Trim();
        if (string.IsNullOrEmpty(description))
            return string.IsNullOrEmpty(message) ? error.ErrorType.ToString() : message;
        if (string.IsNullOrEmpty(message) || string.Equals(description, message, StringComparison.Ordinal))
            return description;
        return $"{description}: {message}";
    }

    /// <summary>Binance replies -4046 / -4059 when the margin type or position mode already has the requested value.</summary>
    public static bool IsNoChangeNeeded(Error? error) => error?.Code is -4046 or -4059;

    private static bool IsRateLimit(Error error, int? code) =>
        error is BaseRateLimitError ||
        error.ErrorType is ErrorType.RateLimitRequest or ErrorType.RateLimitOrder or ErrorType.RateLimitConnection or ErrorType.RateLimitSubscription ||
        code is -1003 or -1015 or 429 or 418;

    private static bool IsUnavailable(Error error, int? code) =>
        error is CantConnectError or WebError or TimeoutError ||
        error.ErrorType is ErrorType.NetworkError or ErrorType.Timeout or ErrorType.UnableToConnect or ErrorType.SystemError ||
        code is >= 500 and <= 599 ||
        code is -1001 or -1007 or -1008 or -1016;

    private static ExchangeErrorKind MapKind(ErrorType type, int? code) => type switch
    {
        ErrorType.UnknownOrder => ExchangeErrorKind.UnknownOrder,
        ErrorType.DuplicateClientOrderId => ExchangeErrorKind.DuplicateClientOrderId,
        ErrorType.InvalidQuantity => ExchangeErrorKind.InvalidQuantity,
        ErrorType.InvalidPrice => ExchangeErrorKind.InvalidPrice,
        ErrorType.InvalidStopParameters => ExchangeErrorKind.InvalidStop,
        ErrorType.InsufficientBalance => ExchangeErrorKind.InsufficientBalance,
        ErrorType.RejectedOrderConfiguration => ExchangeErrorKind.RejectedOrder,
        ErrorType.MaxPosition => ExchangeErrorKind.RejectedOrder,
        ErrorType.NoPosition => ExchangeErrorKind.NoPosition,
        ErrorType.IncorrectState => ExchangeErrorKind.IncorrectState,
        ErrorType.InvalidTimestamp => ExchangeErrorKind.InvalidTimestamp,
        ErrorType.UnknownSymbol or ErrorType.UnavailableSymbol or ErrorType.UnknownAsset => ExchangeErrorKind.UnknownSymbol,
        ErrorType.InvalidParameter or ErrorType.MissingParameter or ErrorType.InvalidOperation => ExchangeErrorKind.InvalidParameter,
        _ => code switch
        {
            -2013 or -2011 => ExchangeErrorKind.UnknownOrder,
            -2010 => ExchangeErrorKind.RejectedOrder,
            -2019 => ExchangeErrorKind.InsufficientBalance,
            -2022 => ExchangeErrorKind.NoPosition,
            -1021 => ExchangeErrorKind.InvalidTimestamp,
            -1121 => ExchangeErrorKind.UnknownSymbol,
            -1111 or -1013 => ExchangeErrorKind.InvalidQuantity,
            -4061 or -4059 or -4046 or -4067 => ExchangeErrorKind.IncorrectState,
            _ => ExchangeErrorKind.Unknown,
        },
    };

    private static DateTimeOffset? ParseBannedUntil(string message)
    {
        var match = BannedUntilPattern().Match(message);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
    }

    [GeneratedRegex(@"until (\d{13})")]
    private static partial Regex BannedUntilPattern();
}
