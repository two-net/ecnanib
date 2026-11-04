using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>
/// Claude, through the Messages API. See The four providers in docs/architecture.md.
/// </summary>
internal sealed class AnthropicAdapter : IAiProviderAdapter
{
    private const string ApiVersion = "2023-06-01";

    /// <summary>The API's largest page.</summary>
    private const int ModelPageSize = 1000;

    /// <summary>
    /// Searches per call. It bounds what searches cost, and keeps the server's own loop well inside the
    /// 10 turns after which it pauses the answer.
    /// </summary>
    private const int MaximumSearches = 5;

    public string Name => "Anthropic";

    public HttpRequestMessage ListModels(SecretValue apiKey, string? pageToken) =>
        Authorize(
            AdapterJson.Get(
                "/v1/models?limit=" + ModelPageSize.ToString(CultureInfo.InvariantCulture)
                + (pageToken is null ? "" : "&after_id=" + Uri.EscapeDataString(pageToken))),
            apiKey);

    public AiModelPage ReadModels(byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var root = document.RootElement;
        var models = root.Required("data").EnumerateArray()
            .Select(model => new AiModel(
                model.String("id")!,
                model.String("display_name"),
                model.Time("created_at"),
                model.Int("max_input_tokens"),
                model.Int("max_tokens")))
            .ToList();

        var next = root.Bool("has_more")
            ? root.String("last_id") ?? throw new JsonException("The model list has more pages but no last ID.")
            : null;

        return new AiModelPage(models, next);
    }

    /// <remarks>
    /// Web search always carries citations, which structured output refuses with a 400. A request that
    /// asks for both gets the search, and the council validates the answer against the schema itself.
    /// </remarks>
    public HttpRequestMessage Complete(SecretValue apiKey, AiRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["messages"] = new JsonArray([.. request.Messages.Select(Message)]),
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            body["system"] = request.Instructions;
        }

        if (request.LiveSearch)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search_20250305",
                ["name"] = "web_search",
                ["max_uses"] = MaximumSearches,
            });
        }
        else if (request.Schema is { } schema)
        {
            body["output_config"] = new JsonObject
            {
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = AdapterJson.Node(schema) },
            };
        }

        return Authorize(AdapterJson.Post("/v1/messages", body), apiKey);
    }

    /// <remarks>
    /// The answer is the text after the last block of another kind. With search, the model may write a
    /// sentence before it searches, and that isn't part of the answer.
    /// </remarks>
    public AiCompletion ReadCompletion(byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var root = document.RootElement;
        var blocks = root.Required("content").EnumerateArray().ToList();
        var usage = root.Required("usage");
        var start = blocks.FindLastIndex(block => block.String("type") != "text") + 1;
        var text = string.Concat(blocks.Skip(start).Select(block => block.String("text")));
        var stop = root.String("stop_reason");

        return new AiCompletion(
            text,
            new AiUsage(
                usage.Long("input_tokens") + usage.Long("cache_creation_input_tokens") + usage.Long("cache_read_input_tokens"),
                usage.Long("output_tokens")),
            Truncated: stop is "max_tokens" or "pause_turn" or "model_context_window_exceeded",
            Refused: stop == "refusal");
    }

    public AiProviderError? ReadError(int statusCode, byte[] body)
    {
        using var document = AdapterJson.Parse(body);

        if (document.RootElement.Object("error") is not { } error)
        {
            return null;
        }

        var type = error.String("type");
        var failure = type switch
        {
            "overloaded_error" => AiFailure.Overloaded,
            "rate_limit_error" => AiFailure.RateLimit,
            "authentication_error" or "permission_error" => AiFailure.Authentication,
            _ => (AiFailure?)null,
        };

        return new AiProviderError(failure, type, error.String("message"));
    }

    private static JsonNode Message(AiMessage message) => new JsonObject
    {
        ["role"] = message.Speaker == AiSpeaker.User ? "user" : "assistant",
        ["content"] = new JsonArray([.. message.Content.Select(Part)]),
    };

    private static JsonNode Part(AiContent content) => content switch
    {
        AiText text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        AiImage image => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = image.MediaType,
                ["data"] = AdapterJson.Base64(image),
            },
        },
        _ => throw new ArgumentException($"The Anthropic adapter can't send {content.GetType().Name}.", nameof(content)),
    };

    private static HttpRequestMessage Authorize(HttpRequestMessage request, SecretValue apiKey)
    {
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Reveal());
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

        return request;
    }
}
