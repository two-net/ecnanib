using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>How one provider's Responses API differs from OpenAI's.</summary>
/// <param name="SearchTools">The server-side tools that give the model live search.</param>
/// <param name="HideInlineCitations">Asks for no citations in the text, which would break a JSON answer.</param>
/// <param name="ReasoningCountedApart">output_tokens leaves out reasoning, which is billed as output all the same.</param>
internal sealed record ResponsesDialect(IReadOnlyList<string> SearchTools, bool HideInlineCitations, bool ReasoningCountedApart);

/// <summary>
/// The Responses API, which OpenAI and xAI both speak. Nothing is stored at the provider: both keep a
/// response for 30 days unless told not to, and every call here stands alone.
/// </summary>
internal static class ResponsesFormat
{
    /// <summary>The least max_output_tokens OpenAI takes.</summary>
    private const int MinimumOutputTokens = 16;

    public static HttpRequestMessage Complete(SecretValue apiKey, AiRequest request, ResponsesDialect dialect)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = new JsonArray([.. request.Messages.Select(Message)]),
            ["max_output_tokens"] = Math.Max(request.MaxOutputTokens, MinimumOutputTokens),
            ["store"] = false,
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            body["instructions"] = request.Instructions;
        }

        if (request.Schema is { } schema)
        {
            // Strict, or OpenAI treats the schema as a suggestion. See the schema rules in docs/architecture.md.
            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = schema.Name,
                    ["schema"] = AdapterJson.Node(schema),
                    ["strict"] = true,
                },
            };
        }

        if (request.LiveSearch)
        {
            body["tools"] = new JsonArray([.. dialect.SearchTools.Select(tool => (JsonNode)new JsonObject { ["type"] = tool })]);

            if (dialect.HideInlineCitations)
            {
                body["include"] = new JsonArray("no_inline_citations");
            }
        }

        return Bearer(AdapterJson.Post("/v1/responses", body), apiKey);
    }

    /// <remarks>
    /// The answer is the last message item's output text. Reasoning and search calls are items of other
    /// kinds, and the raw JSON has no output_text field of its own: that's only in the SDKs.
    /// </remarks>
    /// <exception cref="JsonException">The response failed, or isn't a response.</exception>
    public static AiCompletion ReadCompletion(byte[] body, ResponsesDialect dialect)
    {
        using var document = AdapterJson.Parse(body);
        var root = document.RootElement;
        var output = root.Required("output").EnumerateArray().ToList();
        var usage = root.Required("usage");
        var status = root.String("status");

        if (status is "failed" or "cancelled")
        {
            throw new JsonException($"The response is {status}.");
        }

        var parts = output.LastOrDefault(item => item.String("type") == "message").Array("content").ToList();
        var text = string.Concat(parts.Where(part => part.String("type") == "output_text").Select(part => part.String("text")));
        var reason = root.Object("incomplete_details").String("reason");
        var refused = reason == "content_filter" || parts.Any(part => part.String("type") == "refusal");
        var outputTokens = usage.Long("output_tokens")
            + (dialect.ReasoningCountedApart ? usage.Object("output_tokens_details").Long("reasoning_tokens") : 0);

        return new AiCompletion(
            text,
            new AiUsage(usage.Long("input_tokens"), outputTokens),
            Truncated: status == "incomplete" && !refused,
            Refused: refused);
    }

    /// <summary>A key on the Authorization header, as both providers take it.</summary>
    public static HttpRequestMessage Bearer(HttpRequestMessage request, SecretValue apiKey)
    {
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Reveal());

        return request;
    }

    /// <summary>
    /// A user turn is a list of parts. The model's own earlier turn is plain text, which is the form both
    /// providers document for history sent back without an ID.
    /// </summary>
    private static JsonNode Message(AiMessage message) => message.Speaker == AiSpeaker.User
        ? new JsonObject { ["role"] = "user", ["content"] = new JsonArray([.. message.Content.Select(Part)]) }
        : new JsonObject { ["role"] = "assistant", ["content"] = AdapterJson.Text(message) };

    private static JsonNode Part(AiContent content) => content switch
    {
        AiText text => new JsonObject { ["type"] = "input_text", ["text"] = text.Text },
        AiImage image => new JsonObject { ["type"] = "input_image", ["image_url"] = AdapterJson.DataUrl(image) },
        _ => throw new ArgumentException($"A Responses API adapter can't send {content.GetType().Name}.", nameof(content)),
    };
}
