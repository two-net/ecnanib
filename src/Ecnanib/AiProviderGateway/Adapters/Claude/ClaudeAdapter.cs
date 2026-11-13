using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Claude;

/// <summary>
/// Calls a model of Claude: <c>POST /v1/messages</c>, with the key in <c>x-api-key</c>. The
/// structured result is asked for with <c>output_config.format</c>, which holds the schema. A
/// wrong key is answered with 401, and a model that Claude does not have with 404 and the error
/// <c>not_found_error</c>.
/// </summary>
/// <remarks>
/// A request that Claude refuses is not sent on to another model, which the provider offers to
/// do: the system never puts another model in the place of the one that was chosen (FR-CAT-8).
/// </remarks>
internal sealed class ClaudeAdapter(ModelCall call) : IAiProviderAdapter, IModelCallFormat
{
    // The most tokens an answer may have, which the provider requires to be said. A model that
    // reasons before it answers spends them on that too, so the room is for both. An answer that
    // needs more is cut short, and is then no result.
    private const int MostTokens = 16_000;

    private const string NoSuchModel = "not_found_error";

    public AiProviderCode Provider => ClaudeApi.Provider;

    public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, request, apiKey, cancellationToken);

    public string PathOf(string model) => "v1/messages";

    public void Authorize(HttpRequestHeaders headers, string apiKey) => ClaudeApi.Authorize(headers, apiKey);

    public JsonObject BodyOf(AiCallRequest request, JsonNode schema) => new()
    {
        ["model"] = request.Model,
        ["max_tokens"] = MostTokens,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.Prompt }),
        ["output_config"] = new JsonObject
        {
            ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema },
        },
    };

    public ModelAnswer? Read(JsonElement answer)
    {
        if (answer.Member("content") is not { ValueKind: JsonValueKind.Array })
        {
            return null;
        }

        var reason = answer.Text("stop_reason");
        var end = reason switch
        {
            "end_turn" => AnswerEnd.Complete,
            "max_tokens" or "model_context_window_exceeded" => AnswerEnd.Truncated,
            "refusal" => AnswerEnd.Refused,
            _ => AnswerEnd.Other,
        };

        // What the model reasoned comes before its answer, in blocks of another type.
        var text = string.Concat(
            answer.Items("content").Where(block => block.Text("type") == "text").Select(block => block.Text("text")));

        return new ModelAnswer(end, text, UsageOf(answer.Member("usage")), reason);
    }

    public AiCallOutcome OutcomeOfError(HttpStatusCode status, JsonElement? error) => status switch
    {
        HttpStatusCode.Unauthorized => AiCallOutcome.AuthenticationFailed,
        HttpStatusCode.NotFound when error.Member("error").Text("type") == NoSuchModel => AiCallOutcome.ModelNotFound,
        _ => AiCallOutcome.Failed,
    };

    /// <summary>
    /// The usage of an answer. <c>input_tokens</c> counts only what was not read from or written
    /// to a cache, so the input is the three counts together.
    /// </summary>
    private static AiTokenUsage? UsageOf(JsonElement? usage) =>
        usage.Count("input_tokens") is { } input && usage.Count("output_tokens") is { } output
            ? new AiTokenUsage(
                input + (usage.Count("cache_creation_input_tokens") ?? 0) + (usage.Count("cache_read_input_tokens") ?? 0),
                output)
            : null;
}
