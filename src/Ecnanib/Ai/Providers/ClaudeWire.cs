using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Ai.Providers;

// Anthropic Messages API: POST /messages, GET /models. Wire shapes never leave this folder.

internal sealed record ClaudeMessageRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("messages")] IReadOnlyList<ClaudeMessage> Messages,
    [property: JsonPropertyName("output_config")] ClaudeOutputConfig? OutputConfig);

internal sealed record ClaudeMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ClaudeOutputConfig([property: JsonPropertyName("format")] ClaudeOutputFormat Format);

internal sealed record ClaudeOutputFormat(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("schema")] JsonElement Schema);

internal sealed record ClaudeMessageResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("content")] IReadOnlyList<ClaudeContentBlock>? Content,
    [property: JsonPropertyName("usage")] ClaudeUsage? Usage);

internal sealed record ClaudeContentBlock(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text);

internal sealed record ClaudeUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] int? CacheReadInputTokens);

internal sealed record ClaudeModelList(
    [property: JsonPropertyName("data")] IReadOnlyList<JsonElement>? Data,
    [property: JsonPropertyName("has_more")] bool? HasMore,
    [property: JsonPropertyName("last_id")] string? LastId);

/// <summary>Anthropic error bodies: <c>{"type":"error","error":{"type":"...","message":"..."}}</c>.</summary>
internal sealed class ClaudeErrorParser : IAiErrorBodyParser
{
    public static ClaudeErrorParser Instance { get; } = new();

    public ProviderError? TryParse(JsonElement root) =>
        root.GetObjectOrNull("error") is { } error
            ? new ProviderError(error.GetStringOrNull("type"), error.GetStringOrNull("message"))
            : null;

    public AiErrorKind? Classify(HttpStatusCode status, ProviderError? error, string? model)
    {
        var message = error?.Message ?? string.Empty;
        return error?.Code switch
        {
            "authentication_error" or "permission_error" => AiErrorKind.Unauthorized,
            "rate_limit_error" => AiErrorKind.RateLimited,
            "overloaded_error" => AiErrorKind.Overloaded,
            "api_error" => AiErrorKind.ServerError,
            "request_too_large" => AiErrorKind.ContextTooLong,
            "not_found_error" when message.StartsWith("model", StringComparison.OrdinalIgnoreCase) => AiErrorKind.UnknownModel,
            "invalid_request_error" when message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) => AiErrorKind.QuotaExceeded,
            "invalid_request_error" when message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) => AiErrorKind.ContextTooLong,
            _ => null,
        };
    }
}
