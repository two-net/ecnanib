using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>
/// Gemini, through generateContent. Google calls it legacy beside its newer Interactions API, but still
/// supports it fully. See The four providers in docs/architecture.md.
/// </summary>
internal sealed class GoogleAdapter : IAiProviderAdapter
{
    /// <summary>The API's largest page.</summary>
    private const int ModelPageSize = 1000;

    private const string ModelPrefix = "models/";

    /// <summary>
    /// Models that list generateContent but don't write text: speech, images, live audio and robotics.
    /// Embedding, Imagen, Veo and AQA are here too, in case they ever list it.
    /// </summary>
    private static readonly string[] NonTextModels = ["embedding", "aqa", "imagen", "veo", "-tts", "-image", "-live", "-audio", "robotics"];

    /// <summary>Finish reasons that mean a filter stopped the answer, so another attempt may get one.</summary>
    private static readonly string[] Blocked =
        ["SAFETY", "PROHIBITED_CONTENT", "BLOCKLIST", "SPII", "RECITATION", "IMAGE_SAFETY", "IMAGE_PROHIBITED_CONTENT"];

    public string Name => "Google";

    public HttpRequestMessage ListModels(SecretValue apiKey, string? pageToken) =>
        Authorize(
            AdapterJson.Get(
                "/v1beta/models?pageSize=" + ModelPageSize.ToString(CultureInfo.InvariantCulture)
                + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken))),
            apiKey);

    public AiModelPage ReadModels(byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var root = document.RootElement;

        // A key that can use no models gets an empty object, with no models field at all.
        var models = root.Array("models")
            .Where(model => model.Strings("supportedGenerationMethods").Contains("generateContent", StringComparer.Ordinal))
            .Select(model => (Id: Id(model.String("name")), Model: model))
            .Where(entry => !NonTextModels.Any(family => entry.Id.Contains(family, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => new AiModel(
                entry.Id,
                entry.Model.String("displayName"),
                ContextWindow: entry.Model.Int("inputTokenLimit"),
                MaxOutputTokens: entry.Model.Int("outputTokenLimit"))
            {
                // A model that thinks takes a thinking level or budget.
                Capabilities = new AiCapabilities(ReasoningEffort: entry.Model.OptionalBool("thinking")),
            })
            .ToList();

        return new AiModelPage(models, root.String("nextPageToken"));
    }

    /// <remarks>
    /// Gemini combines Google Search with structured output only on Gemini 3 and later, as a preview. A
    /// request that asks for both gets the search, and the council validates the answer itself.
    /// </remarks>
    public HttpRequestMessage Complete(SecretValue apiKey, AiRequest request)
    {
        var generation = new JsonObject { ["maxOutputTokens"] = request.MaxOutputTokens };
        var body = new JsonObject
        {
            ["contents"] = new JsonArray([.. request.Messages.Select(Message)]),
            ["generationConfig"] = generation,
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Instructions }) };
        }

        if (request.LiveSearch)
        {
            body["tools"] = new JsonArray(new JsonObject { ["googleSearch"] = new JsonObject() });
        }
        else if (request.Schema is { } schema)
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = AdapterJson.Node(schema);
        }

        // Escaped, so that an odd model ID can't change the path.
        return Authorize(AdapterJson.Post($"/v1beta/models/{Uri.EscapeDataString(request.Model)}:generateContent", body), apiKey);
    }

    public AiCompletion ReadCompletion(byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var root = document.RootElement;
        var metadata = root.Object("usageMetadata");

        // Thoughts are counted apart from the answer, and billed as output.
        var usage = new AiUsage(
            metadata.Long("promptTokenCount") + metadata.Long("toolUsePromptTokenCount"),
            metadata.Long("candidatesTokenCount") + metadata.Long("thoughtsTokenCount"));

        if (root.Array("candidates").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } candidate)
        {
            // A blocked prompt gets no candidates at all.
            return root.Object("promptFeedback").String("blockReason") is not null
                ? new AiCompletion("", usage, Refused: true)
                : throw new JsonException("The answer has no candidates and no block reason.");
        }

        var text = string.Concat(candidate.Object("content").Array("parts")
            .Where(part => !part.Bool("thought"))
            .Select(part => part.String("text")));
        var finish = candidate.String("finishReason");

        return new AiCompletion(
            text,
            usage,
            Truncated: finish is "MAX_TOKENS" or "CONTINUATION",
            Refused: finish is not null && Blocked.Contains(finish, StringComparer.Ordinal));
    }

    /// <remarks>
    /// An invalid key comes back as 400 INVALID_ARGUMENT, which the status alone would call an invalid
    /// request, so the reason in its details decides.
    /// </remarks>
    public AiProviderError? ReadError(int statusCode, byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            root = root.EnumerateArray().FirstOrDefault();
        }

        if (root.Object("error") is not { } error)
        {
            return null;
        }

        var status = error.String("status");
        var reason = error.Array("details").Select(detail => detail.String("reason")).FirstOrDefault(value => value is not null);
        var failure = reason == "API_KEY_INVALID"
            ? AiFailure.Authentication
            : status switch
            {
                "UNAUTHENTICATED" or "PERMISSION_DENIED" => AiFailure.Authentication,
                "RESOURCE_EXHAUSTED" => AiFailure.RateLimit,
                "UNAVAILABLE" => AiFailure.Overloaded,
                "DEADLINE_EXCEEDED" => AiFailure.Timeout,
                _ => (AiFailure?)null,
            };

        return new AiProviderError(failure, reason ?? status, error.String("message"));
    }

    /// <exception cref="JsonException">A model has no name.</exception>
    private static string Id(string? name) => name switch
    {
        null => throw new JsonException("A model has no name."),
        _ when name.StartsWith(ModelPrefix, StringComparison.Ordinal) => name[ModelPrefix.Length..],
        _ => name,
    };

    private static JsonNode Message(AiMessage message) => new JsonObject
    {
        ["role"] = message.Speaker == AiSpeaker.User ? "user" : "model",
        ["parts"] = new JsonArray([.. message.Content.Select(Part)]),
    };

    private static JsonNode Part(AiContent content) => content switch
    {
        AiText text => new JsonObject { ["text"] = text.Text },
        AiImage image => new JsonObject
        {
            ["inlineData"] = new JsonObject { ["mimeType"] = image.MediaType, ["data"] = AdapterJson.Base64(image) },
        },
        _ => throw new ArgumentException($"The Google adapter can't send {content.GetType().Name}.", nameof(content)),
    };

    /// <summary>The key goes in a header, never in the URI as ?key=, because a URI can reach an exception.</summary>
    private static HttpRequestMessage Authorize(HttpRequestMessage request, SecretValue apiKey)
    {
        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Reveal());

        return request;
    }
}
