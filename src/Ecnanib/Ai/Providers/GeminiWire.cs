using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Ai.Providers;

// Gemini API: POST /models/{model}:generateContent, GET /models.

internal sealed record GeminiGenerateRequest(
    [property: JsonPropertyName("systemInstruction")] GeminiContent? SystemInstruction,
    [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents,
    [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

internal sealed record GeminiContent(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart>? Parts);

internal sealed record GeminiPart(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("thought")] bool? Thought);

internal sealed record GeminiGenerationConfig(
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("responseMimeType")] string? ResponseMimeType,
    [property: JsonPropertyName("responseJsonSchema")] JsonElement? ResponseJsonSchema);

internal sealed record GeminiGenerateResponse(
    [property: JsonPropertyName("candidates")] IReadOnlyList<GeminiCandidate>? Candidates,
    [property: JsonPropertyName("promptFeedback")] GeminiPromptFeedback? PromptFeedback,
    [property: JsonPropertyName("usageMetadata")] GeminiUsageMetadata? UsageMetadata,
    [property: JsonPropertyName("modelVersion")] string? ModelVersion,
    [property: JsonPropertyName("responseId")] string? ResponseId);

internal sealed record GeminiCandidate(
    [property: JsonPropertyName("content")] GeminiContent? Content,
    [property: JsonPropertyName("finishReason")] string? FinishReason);

internal sealed record GeminiPromptFeedback([property: JsonPropertyName("blockReason")] string? BlockReason);

internal sealed record GeminiUsageMetadata(
    [property: JsonPropertyName("promptTokenCount")] int? PromptTokenCount,
    [property: JsonPropertyName("candidatesTokenCount")] int? CandidatesTokenCount,
    [property: JsonPropertyName("totalTokenCount")] int? TotalTokenCount,
    [property: JsonPropertyName("thoughtsTokenCount")] int? ThoughtsTokenCount,
    [property: JsonPropertyName("cachedContentTokenCount")] int? CachedContentTokenCount);

internal sealed record GeminiModelList(
    [property: JsonPropertyName("models")] IReadOnlyList<JsonElement>? Models,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

/// <summary>
/// Google error bodies <c>{"error":{"code","message","status","details":[...]}}</c>. An invalid key arrives as HTTP 400
/// <c>INVALID_ARGUMENT</c> with an <c>ErrorInfo</c> reason starting <c>API_KEY_</c>, so it is classified here, not by status.
/// A 429 whose <c>QuotaFailure</c> names a per-day quota is exhaustion, not throttling: waiting minutes will not help.
/// </summary>
internal sealed class GeminiErrorParser : IAiErrorBodyParser
{
    public static GeminiErrorParser Instance { get; } = new();

    public ProviderError? TryParse(JsonElement root)
    {
        if (root.GetObjectOrNull("error") is not { } error)
            return null;

        string? reason = null;
        TimeSpan? retryDelay = null;
        var dailyQuota = false;
        foreach (var detail in error.EnumerateArrayOrEmpty("details"))
        {
            var type = detail.GetStringOrNull("@type") ?? string.Empty;
            if (type.EndsWith("ErrorInfo", StringComparison.Ordinal))
                reason ??= detail.GetStringOrNull("reason");
            else if (type.EndsWith("RetryInfo", StringComparison.Ordinal))
                retryDelay ??= RetryAfterParser.FromDuration(detail.GetStringOrNull("retryDelay"));
            else if (type.EndsWith("QuotaFailure", StringComparison.Ordinal))
                dailyQuota |= detail.EnumerateArrayOrEmpty("violations").Any(v => (v.GetStringOrNull("quotaId") ?? string.Empty).Contains("PerDay", StringComparison.OrdinalIgnoreCase));
        }

        return new ProviderError(error.GetStringOrNull("status"), error.GetStringOrNull("message"), Reason: reason, RetryDelay: retryDelay, DailyQuota: dailyQuota);
    }

    public AiErrorKind? Classify(HttpStatusCode status, ProviderError? error, string? model)
    {
        if (error is null)
            return null;

        var message = error.Message ?? string.Empty;
        if (error.Reason?.StartsWith("API_KEY_", StringComparison.Ordinal) == true ||
            message.StartsWith("API key not valid", StringComparison.OrdinalIgnoreCase))
        {
            return AiErrorKind.Unauthorized;
        }

        return error.Code switch
        {
            "UNAUTHENTICATED" or "PERMISSION_DENIED" => AiErrorKind.Unauthorized,
            "RESOURCE_EXHAUSTED" => error.DailyQuota ? AiErrorKind.QuotaExceeded : AiErrorKind.RateLimited,
            "UNAVAILABLE" => AiErrorKind.Overloaded,
            "INTERNAL" => AiErrorKind.ServerError,
            "DEADLINE_EXCEEDED" => AiErrorKind.Timeout,
            "NOT_FOUND" when model is not null => AiErrorKind.UnknownModel,
            "INVALID_ARGUMENT" when message.Contains("token count", StringComparison.OrdinalIgnoreCase) && message.Contains("exceeds", StringComparison.OrdinalIgnoreCase) => AiErrorKind.ContextTooLong,
            _ => null,
        };
    }
}
