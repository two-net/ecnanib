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

    /// <summary>Reads an answer: the text of its message, how it ended, and its usage.</summary>
    public static ModelAnswer? Read(JsonElement answer)
    {
        var status = answer.Text("status");

        // An answer that the provider itself calls failed is a failed call, and not an answer.
        if (answer.Member("output") is not { ValueKind: JsonValueKind.Array } || status == "failed")
        {
            return null;
        }

        var parts = answer.Items("output")
            .Where(item => item.Text("type") == "message")
            .SelectMany(message => message.Items("content"))
            .ToList();

        var text = string.Concat(parts.Where(part => part.Text("type") == "output_text").Select(part => part.Text("text")));
        var usage = UsageOf(answer.Member("usage"));

        // A model that will not answer says so in a part of its own, in an answer that is complete.
        if (parts.Any(part => part.Text("type") == "refusal"))
        {
            return new ModelAnswer(AnswerEnd.Refused, text, usage, "refusal");
        }

        var cause = answer.Member("incomplete_details").Text("reason");

        return status switch
        {
            "completed" => new ModelAnswer(AnswerEnd.Complete, text, usage, status),
            "incomplete" when cause == "max_output_tokens" => new ModelAnswer(AnswerEnd.Truncated, text, usage, cause),
            "incomplete" when cause == "content_filter" => new ModelAnswer(AnswerEnd.Refused, text, usage, cause),
            _ => new ModelAnswer(AnswerEnd.Other, text, usage, cause ?? status),
        };
    }

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
