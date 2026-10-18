using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Ai.Providers;

// OpenAI Responses API (also served by xAI): POST /responses; OpenAI GET /models; xAI GET /language-models.

internal sealed record ResponsesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
    [property: JsonPropertyName("store")] bool Store,
    [property: JsonPropertyName("text")] ResponsesTextOptions? Text);

internal sealed record ResponsesTextOptions([property: JsonPropertyName("format")] ResponsesTextFormat Format);

internal sealed record ResponsesTextFormat(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("schema")] JsonElement Schema,
    [property: JsonPropertyName("strict")] bool Strict);

internal sealed record ResponsesResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("incomplete_details")] ResponsesIncompleteDetails? IncompleteDetails,
    [property: JsonPropertyName("error")] ResponsesError? Error,
    [property: JsonPropertyName("output")] IReadOnlyList<ResponsesOutputItem>? Output,
    [property: JsonPropertyName("usage")] ResponsesUsage? Usage);

internal sealed record ResponsesIncompleteDetails([property: JsonPropertyName("reason")] string? Reason);

internal sealed record ResponsesError(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record ResponsesOutputItem(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("content")] IReadOnlyList<ResponsesContentPart>? Content);

internal sealed record ResponsesContentPart(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("refusal")] string? Refusal);

internal sealed record ResponsesUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens,
    [property: JsonPropertyName("input_tokens_details")] ResponsesInputTokensDetails? InputTokensDetails,
    [property: JsonPropertyName("output_tokens_details")] ResponsesOutputTokensDetails? OutputTokensDetails);

internal sealed record ResponsesInputTokensDetails([property: JsonPropertyName("cached_tokens")] int? CachedTokens);

internal sealed record ResponsesOutputTokensDetails([property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens);

internal sealed record OpenAiModelList([property: JsonPropertyName("data")] IReadOnlyList<JsonElement>? Data);

internal sealed record XaiLanguageModelList([property: JsonPropertyName("models")] IReadOnlyList<JsonElement>? Models);

/// <summary>
/// OpenAI error bodies <c>{"error":{"message","type","param","code"}}</c>, and xAI's flat variant
/// <c>{"code":"...","error":"message"}</c>.
/// </summary>
internal sealed class OpenAiErrorParser : IAiErrorBodyParser
{
    public static OpenAiErrorParser Instance { get; } = new();

    public ProviderError? TryParse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
            return null;

        return error.ValueKind switch
        {
            JsonValueKind.Object => new ProviderError(
                error.GetStringOrNull("code") ?? error.GetStringOrNull("type"),
                error.GetStringOrNull("message"),
                error.GetStringOrNull("param")),
            JsonValueKind.String => new ProviderError(root.GetStringOrNull("code"), error.GetString()),
            _ => null,
        };
    }

    public AiErrorKind? Classify(HttpStatusCode status, ProviderError? error, string? model)
    {
        if (error is null)
            return null;
        // xAI reports a wrong key as HTTP 400 "Client specified an invalid argument" / "Incorrect API key provided".
        if ((int)status is >= 400 and < 500 && AiErrorTranslator.MentionsInvalidApiKey(error.Message))
            return AiErrorKind.Unauthorized;
        if (string.Equals(error.Param, "model", StringComparison.OrdinalIgnoreCase) && (int)status is >= 400 and < 500)
            return AiErrorKind.UnknownModel;

        return error.Code switch
        {
            "invalid_api_key" => AiErrorKind.Unauthorized,
            "insufficient_quota" => AiErrorKind.QuotaExceeded,
            "rate_limit_exceeded" => AiErrorKind.RateLimited,
            "model_not_found" => AiErrorKind.UnknownModel,
            "context_length_exceeded" => AiErrorKind.ContextTooLong,
            "server_error" => AiErrorKind.ServerError,
            _ => null,
        };
    }
}
