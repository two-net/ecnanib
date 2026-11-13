using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Grok;

/// <summary>
/// Calls a model of Grok: <c>POST /v1/responses</c>, with the key as a bearer token. The
/// structured result is asked for with <c>text.format</c>: see <see cref="ResponsesFormat"/>.
/// </summary>
/// <remarks>
/// Grok does not document the body of its errors, so its answers are told by what it has been
/// seen to send: an object whose <c>error</c> is the message, as text. A wrong key is answered
/// with 401, or with 400 and a message about the API key. Unlike the check of a key, a call to a
/// model has parameters that can be wrong, so a 400 alone says nothing about the key. A model
/// that Grok does not have is answered with 404 and such an error.
/// </remarks>
internal sealed class GrokAdapter(ModelCall call) : IAiProviderAdapter, IModelCallFormat
{
    private const string AboutTheKey = "API key";

    public AiProviderCode Provider => GrokApi.Provider;

    public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, request, apiKey, cancellationToken);

    public string PathOf(string model) => ResponsesFormat.Path;

    public void Authorize(HttpRequestHeaders headers, string apiKey) => GrokApi.Authorize(headers, apiKey);

    public JsonObject BodyOf(AiCallRequest request, JsonNode schema) => ResponsesFormat.BodyOf(request, schema);

    public ModelAnswer? Read(JsonElement answer) => ResponsesFormat.Read(answer);

    public AiCallOutcome OutcomeOfError(HttpStatusCode status, JsonElement? error) => status switch
    {
        HttpStatusCode.Unauthorized => AiCallOutcome.AuthenticationFailed,
        HttpStatusCode.BadRequest when MessageOf(error)?.Contains(AboutTheKey, StringComparison.OrdinalIgnoreCase) == true
            => AiCallOutcome.AuthenticationFailed,
        HttpStatusCode.NotFound when MessageOf(error) is not null => AiCallOutcome.ModelNotFound,
        _ => AiCallOutcome.Failed,
    };

    /// <summary>
    /// The message of an error: <c>error</c> as text, or <c>error.message</c> where Grok answers
    /// in the form that GPT uses. It is read to tell what the error is about, and goes nowhere.
    /// </summary>
    private static string? MessageOf(JsonElement? error) =>
        error.Text("error") ?? error.Member("error").Text("message");
}
