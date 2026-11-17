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
/// <para>
/// A request that Claude refuses is not sent on to another model, which the provider offers to
/// do: the system never puts another model in the place of the one that was chosen (FR-CAT-8).
/// </para>
/// <para>
/// A model searches live with the web search of the provider, which runs on the provider's side
/// and is offered as a tool of the request (FR-CYC-3). It is the first version of that tool: the
/// model calls it directly, and the later versions run its results through code first, which not
/// every model and not every platform of the provider has.
/// </para>
/// </remarks>
internal sealed class ClaudeAdapter(ModelCall call) : IAiProviderAdapter, IModelCallFormat
{
    // The most tokens an answer may have, which the provider requires to be said. A model that
    // reasons before it answers spends them on that too, so the room is for both. An answer that
    // needs more is cut short, and is then no result.
    private const int MostTokens = 16_000;

    // The most searches one call may run, each of which the provider charges for. A search past
    // it is answered to the model with an error of the tool, and the model answers from what it
    // has by then.
    private const int MostSearches = 5;

    private const string WebSearch = "web_search_20250305";

    private const string NoSuchModel = "not_found_error";

    public AiProviderCode Provider => ClaudeApi.Provider;

    public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, request, apiKey, cancellationToken);

    public string PathOf(string model) => "v1/messages";

    public void Authorize(HttpRequestHeaders headers, string apiKey) => ClaudeApi.Authorize(headers, apiKey);

    public JsonObject BodyOf(AiCallRequest request, JsonNode schema)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = MostTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.Prompt }),
            ["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = schema },
            },
        };

        if (request.LiveSearch)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = WebSearch,
                ["name"] = "web_search",
                ["max_uses"] = MostSearches,
            });
        }

        return body;
    }

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

            // A turn that the provider paused in the middle of its searches is among the rest:
            // the call is made once, and what there is of it is no finished answer.
            _ => AnswerEnd.Other,
        };

        // What the model reasoned comes before its answer, in blocks of another type. A model
        // that searches may also say what it is about to look for: its answer is what it wrote
        // after the last block of a tool. Without a tool that is every block of text.
        var blocks = answer.Items("content").ToList();
        var afterTools = blocks.FindLastIndex(block => IsOfTool(block.Text("type"))) + 1;
        var text = string.Concat(
            blocks.Skip(afterTools).Where(block => block.Text("type") == "text").Select(block => block.Text("text")));

        var usage = answer.Member("usage");

        return new ModelAnswer(
            end, text, UsageOf(usage), reason, usage.Member("server_tool_use").Count("web_search_requests") ?? 0);
    }

    /// <summary>Whether a block is the call of a tool or what the tool answered, by its type.</summary>
    private static bool IsOfTool(string? type) =>
        type is "server_tool_use" or "tool_use" || type?.EndsWith("_tool_result", StringComparison.Ordinal) == true;

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
