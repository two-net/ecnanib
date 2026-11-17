using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecnanib.AiProviderGateway.Adapters;

/// <summary>
/// The request and the answer of <c>POST /v1/responses</c>, which GPT and Grok both take. The
/// structured result is asked for with <c>text.format</c>, which holds the schema. What the two
/// providers answer an error with differs, and each adapter says that for itself.
/// </summary>
internal static class ResponsesFormat
{
    public const string Path = "v1/responses";

    // The format must have a name. It names nothing that the caller sees.
    private const string ResultName = "result";

    /// <summary>
    /// The body of a request. <c>store</c> is off: both providers would otherwise keep the prompt
    /// and the answer for thirty days (NFR-SEC-8).
    /// </summary>
    public static JsonObject BodyOf(AiCallRequest request, JsonNode schema) => new()
    {
        ["model"] = request.Model,
        ["input"] = request.Prompt,
        ["text"] = new JsonObject
        {
            ["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = ResultName,
                ["strict"] = true,
                ["schema"] = schema,
            },
        },
        ["store"] = false,
    };

    /// <summary>
    /// The search of the web that both providers run on their side, as a tool of a request. A
    /// model that is offered it searches live when it sees reason to (FR-CYC-3).
    /// </summary>
    public static JsonObject WebSearch() => new() { ["type"] = "web_search" };

    /// <summary>Reads an answer: the text of its message, how it ended, its usage, and the searches it shows.</summary>
    public static ModelAnswer? Read(JsonElement answer)
    {
        var status = answer.Text("status");

        // An answer that the provider itself calls failed is a failed call, and not an answer.
        if (answer.Member("output") is not { ValueKind: JsonValueKind.Array } || status == "failed")
        {
            return null;
        }

        // A model that searches may say what it is about to look for, in a message before the
        // call of the tool: its answer is what it wrote after the last call. Without a call
        // that is every message.
        var items = answer.Items("output").ToList();
        var afterCalls = items.FindLastIndex(item => item.Text("type")?.EndsWith("_call", StringComparison.Ordinal) == true) + 1;

        var parts = items.Skip(afterCalls)
            .Where(item => item.Text("type") == "message")
            .SelectMany(message => message.Items("content"))
            .ToList();

        var text = string.Concat(parts.Where(part => part.Text("type") == "output_text").Select(part => part.Text("text")));
        var usage = UsageOf(answer.Member("usage"));
        var searches = items.Count(IsWebSearch);

        // A model that will not answer says so in a part of its own, in an answer that is complete.
        if (parts.Any(part => part.Text("type") == "refusal"))
        {
            return new ModelAnswer(AnswerEnd.Refused, text, usage, "refusal", searches);
        }

        var cause = answer.Member("incomplete_details").Text("reason");

        return status switch
        {
            "completed" => new ModelAnswer(AnswerEnd.Complete, text, usage, status, searches),
            "incomplete" when cause == "max_output_tokens" => new ModelAnswer(AnswerEnd.Truncated, text, usage, cause, searches),
            "incomplete" when cause == "content_filter" => new ModelAnswer(AnswerEnd.Refused, text, usage, cause, searches),
            _ => new ModelAnswer(AnswerEnd.Other, text, usage, cause ?? status, searches),
        };
    }

    /// <summary>
    /// Whether an item of an answer is a search of the web that the provider ran. Opening a page
    /// that a search found, or looking inside one, is an item of the same type with another
    /// action: neither is a search, and neither is charged as one.
    /// </summary>
    private static bool IsWebSearch(JsonElement item) =>
        item.Text("type") == "web_search_call" && item.Member("action").Text("type") is null or "search";

    /// <summary>
    /// The usage of an answer. The output is what the total holds beyond the input: one provider
    /// counts what the model reasoned inside <c>output_tokens</c> and the other beside it, and
    /// both charge it as output.
    /// </summary>
    private static AiTokenUsage? UsageOf(JsonElement? usage)
    {
        if (usage.Count("input_tokens") is not { } input)
        {
            return null;
        }

        var output = usage.Count("output_tokens") ?? 0;

        return new AiTokenUsage(
            input, usage.Count("total_tokens") is { } total && total - input >= output ? total - input : output);
    }
}
