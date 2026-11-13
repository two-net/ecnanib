using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gemini;

/// <summary>
/// Calls a model of Gemini: <c>POST /v1beta/models/{model}:generateContent</c>, with the key in
/// <c>x-goog-api-key</c>. The structured result is asked for in <c>generationConfig</c>, with
/// the schema as <c>responseJsonSchema</c>. A wrong key is answered with 400 and the reason
/// <c>API_KEY_INVALID</c>, and a model that Gemini does not have with 404 and the status
/// <c>NOT_FOUND</c>.
/// </summary>
internal sealed class GeminiAdapter(ModelCall call) : IAiProviderAdapter, IModelCallFormat
{
    // How the list of models names a model: "models/" and its id. The address takes the id.
    private const string ListPrefix = "models/";

    private const string NoSuchModel = "NOT_FOUND";

    public AiProviderCode Provider => GeminiApi.Provider;

    public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, request, apiKey, cancellationToken);

    /// <summary>The model is part of the address, so it is escaped: an id can say nothing about where the call goes.</summary>
    public string PathOf(string model)
    {
        var id = model.StartsWith(ListPrefix, StringComparison.Ordinal) ? model[ListPrefix.Length..] : model;

        return $"v1beta/models/{Uri.EscapeDataString(id)}:generateContent";
    }

    public void Authorize(HttpRequestHeaders headers, string apiKey) => GeminiApi.Authorize(headers, apiKey);

    public JsonObject BodyOf(AiCallRequest request, JsonNode schema) => new()
    {
        ["contents"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Prompt }),
        }),
        ["generationConfig"] = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseJsonSchema"] = schema,
        },
    };

    public ModelAnswer? Read(JsonElement answer)
    {
        var usage = UsageOf(answer.Member("usageMetadata"));

        if (answer.Items("candidates").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } candidate)
        {
            // Gemini gives no candidate when it blocks the prompt, and says why.
            return answer.Member("promptFeedback") is { } feedback
                ? new ModelAnswer(AnswerEnd.Refused, Text: null, usage, feedback.Text("blockReason"))
                : null;
        }

        var reason = candidate.Text("finishReason");
        var end = reason switch
        {
            "STOP" => AnswerEnd.Complete,
            "MAX_TOKENS" => AnswerEnd.Truncated,
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" => AnswerEnd.Refused,
            _ => AnswerEnd.Other,
        };

        // A part that is marked as a thought is what the model reasoned, and not its answer.
        var text = candidate.Member("content") is { } content
            ? string.Concat(content.Items("parts")
                .Where(part => part.Member("thought") is not { ValueKind: JsonValueKind.True })
                .Select(part => part.Text("text")))
            : null;

        return new ModelAnswer(end, text, usage, reason);
    }

    public AiCallOutcome OutcomeOfError(HttpStatusCode status, JsonElement? error) => status switch
    {
        HttpStatusCode.Unauthorized => AiCallOutcome.AuthenticationFailed,
        HttpStatusCode.BadRequest when GeminiApi.NamesWrongKey(error) => AiCallOutcome.AuthenticationFailed,
        HttpStatusCode.NotFound when error.Member("error").Text("status") == NoSuchModel => AiCallOutcome.ModelNotFound,
        _ => AiCallOutcome.Failed,
    };

    /// <summary>
    /// The usage of an answer. Gemini counts what the model reasoned apart from its answer, and
    /// charges both as output.
    /// </summary>
    private static AiTokenUsage? UsageOf(JsonElement? usage) =>
        usage.Count("promptTokenCount") is { } input
            ? new AiTokenUsage(input, (usage.Count("candidatesTokenCount") ?? 0) + (usage.Count("thoughtsTokenCount") ?? 0))
            : null;
}
