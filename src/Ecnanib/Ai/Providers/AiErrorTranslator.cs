using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Ai.Providers;

/// <summary>What a provider's error body says, in provider-neutral form. <see cref="Message"/> is already scrubbed.</summary>
internal sealed record ProviderError(string? Code, string? Message, string? Param = null, string? Reason = null, TimeSpan? RetryDelay = null, bool DailyQuota = false);

/// <summary>Reads one provider's error body and applies its code-specific classification; the translator does the rest.</summary>
internal interface IAiErrorBodyParser
{
    /// <summary>Never throws; null when the body is not this provider's error shape.</summary>
    ProviderError? TryParse(JsonElement root);

    /// <summary>Provider-specific overrides; null defers to the shared status-based classification.</summary>
    AiErrorKind? Classify(HttpStatusCode status, ProviderError? error, string? model);
}

/// <summary>
/// Turns a failed exchange into the module's typed exceptions. Classification runs in a fixed order: the provider's
/// own overrides, then rate limit, quota, auth, timeout, overload, server error, unknown model, context length and
/// other request errors. Messages carry the provider's (scrubbed) text and code only, never the request URL, headers
/// or key (FR-LOG-05). A body that cannot be parsed falls back to the status alone.
/// </summary>
internal static partial class AiErrorTranslator
{
    private const int MaxDetailLength = 500;

    public static AiException Translate(
        AiProvider provider,
        IAiErrorBodyParser parser,
        string operation,
        string? model,
        HttpStatusCode status,
        string? reasonPhrase,
        HttpResponseHeaders? headers,
        string scrubbedBody,
        DateTimeOffset now)
    {
        var error = TryParseBody(parser, scrubbedBody);
        var detail = error?.Message is { Length: > 0 } message ? message : $"HTTP {(int)status} {reasonPhrase}".TrimEnd();
        var retryAfter = error?.RetryDelay ?? RetryAfterParser.FromHeaders(headers, now) ?? RetryAfterParser.FromMessage(error?.Message);
        var kind = parser.Classify(status, error, model) ?? ClassifyByStatus(status, error, model);
        return Build(provider, operation, model, kind, (int)status, error?.Code, detail, retryAfter, now, RequestId(headers), null);
    }

    /// <summary>A failure the provider reports inside a 2xx body (for example a Responses object with status "failed").</summary>
    public static AiException TranslateInBand(AiProvider provider, IAiErrorBodyParser parser, string operation, string? model, ProviderError error, string? requestId, DateTimeOffset now)
    {
        var kind = parser.Classify(HttpStatusCode.OK, error, model) ?? ClassifyByStatus(HttpStatusCode.OK, error, model);
        if (kind == AiErrorKind.Unknown)
            kind = AiErrorKind.InvalidRequest;
        var detail = string.IsNullOrEmpty(error.Message) ? "the provider reported a failure" : error.Message;
        var retryAfter = error.RetryDelay ?? RetryAfterParser.FromMessage(error.Message);
        return Build(provider, operation, model, kind, null, error.Code, detail, retryAfter, now, requestId, null);
    }

    public static AiException Build(
        AiProvider provider,
        string operation,
        string? model,
        AiErrorKind kind,
        int? statusCode,
        string? providerCode,
        string detail,
        TimeSpan? retryAfter,
        DateTimeOffset now,
        string? requestId,
        Exception? innerException)
    {
        var suffix = !string.IsNullOrEmpty(providerCode) ? $" ({providerCode})" : statusCode is { } s ? $" (HTTP {s})" : string.Empty;
        var body = kind == AiErrorKind.UnknownModel && model is not null && !detail.Contains(model, StringComparison.Ordinal)
            ? $"model '{model}' was rejected: {Truncate(detail)}"
            : Truncate(detail);
        var text = $"{provider} {operation} failed: {body}{suffix}";

        var retryAt = retryAfter is { } wait ? now + wait : (DateTimeOffset?)null;
        return kind switch
        {
            AiErrorKind.RateLimited => new AiRateLimitedException(text, provider, retryAfter, retryAt, statusCode, providerCode, model, requestId, innerException),
            AiErrorKind.Unauthorized => new AiAuthException(text, provider, statusCode, providerCode, model, requestId, innerException),
            AiErrorKind.Network or AiErrorKind.Timeout or AiErrorKind.Overloaded or AiErrorKind.ServerError =>
                new AiUnavailableException(text, provider, kind, statusCode, providerCode, model, requestId, retryAfter, retryAt, innerException),
            _ => new AiRequestException(text, provider, kind, statusCode, providerCode, model, requestId, innerException),
        };
    }

    /// <summary>
    /// Status-based classification. A 404 counts as a rejected model only on a completion whose error body the
    /// provider actually produced (a gateway's HTML 404 is a request error), and message sniffing for unknown models
    /// is skipped when the provider blamed a parameter other than <c>model</c>.
    /// </summary>
    internal static AiErrorKind ClassifyByStatus(HttpStatusCode status, ProviderError? error, string? model)
    {
        var code = (int)status;
        var message = error?.Message;
        var paramIsModel = error?.Param is null || string.Equals(error.Param, "model", StringComparison.OrdinalIgnoreCase);
        if (code == 429)
            return AiErrorKind.RateLimited;
        if (code == 402)
            return AiErrorKind.QuotaExceeded;
        if (code is 401 or 403)
            return AiErrorKind.Unauthorized;
        if (code == 408)
            return AiErrorKind.Timeout;
        if (code is 503 or 529)
            return AiErrorKind.Overloaded;
        if (code >= 500)
            return AiErrorKind.ServerError;
        if (code >= 400)
        {
            if (code == 404 && model is not null && error is not null && paramIsModel)
                return AiErrorKind.UnknownModel;
            if (message is not null && paramIsModel && UnknownModelPattern().IsMatch(message))
                return AiErrorKind.UnknownModel;
            if (message is not null && ContextPattern().IsMatch(message))
                return AiErrorKind.ContextTooLong;
            return AiErrorKind.InvalidRequest;
        }

        return AiErrorKind.Unknown;
    }

    private static ProviderError? TryParseBody(IAiErrorBodyParser parser, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return parser.TryParse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? RequestId(HttpResponseHeaders? headers)
    {
        if (headers is null)
            return null;
        foreach (var name in new[] { "request-id", "x-request-id" })
        {
            if (headers.TryGetValues(name, out var values))
                return values.FirstOrDefault();
        }

        return null;
    }

    /// <summary>Provider text that says the key itself is wrong, for providers that report it with a 400 (xAI) or no code.</summary>
    internal static bool MentionsInvalidApiKey(string? message) =>
        message is not null && InvalidApiKeyPattern().IsMatch(message);

    private static string Truncate(string detail) =>
        detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength] + "…";

    [GeneratedRegex(@"\bmodel\b[^.]*\b(not found|does not exist|doesn't exist|not exist|is not available|is unavailable|is unknown|is invalid|is not supported|is unsupported|was not found)\b|\b(not found|does not exist|no such|unknown|unsupported|invalid|nonexistent)\s+model\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownModelPattern();

    [GeneratedRegex(@"\b(incorrect|invalid|not valid|missing|bad|unknown)\b[^.]*\bapi[ _-]?key\b|\bapi[ _-]?key\b[^.]*\b(incorrect|invalid|not valid|missing|bad|unknown|expired|revoked)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InvalidApiKeyPattern();

    [GeneratedRegex(@"context (length|window)|prompt is too long|too many tokens|token count .*exceeds|maximum context|exceeds the (model's )?(maximum )?(context|token)|input (is )?too long|request_too_large|request too large", RegexOptions.IgnoreCase)]
    private static partial Regex ContextPattern();
}
