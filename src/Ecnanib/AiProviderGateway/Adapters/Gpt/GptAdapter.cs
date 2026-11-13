using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gpt;

/// <summary>
/// Calls a model of GPT: <c>POST /v1/responses</c>, with the key as a bearer token. The
/// structured result is asked for with <c>text.format</c>: see <see cref="ResponsesFormat"/>. A
/// wrong key is answered with 401, and a model that GPT does not have with the error code
/// <c>model_not_found</c>, whatever the status.
/// </summary>
internal sealed class GptAdapter(ModelCall call) : IAiProviderAdapter, IModelCallFormat
{
    private const string NoSuchModel = "model_not_found";

    public AiProviderCode Provider => GptApi.Provider;

    public Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, request, apiKey, cancellationToken);

    public string PathOf(string model) => ResponsesFormat.Path;

    public void Authorize(HttpRequestHeaders headers, string apiKey) => GptApi.Authorize(headers, apiKey);

    public JsonObject BodyOf(AiCallRequest request, JsonNode schema) => ResponsesFormat.BodyOf(request, schema);

    public ModelAnswer? Read(JsonElement answer) => ResponsesFormat.Read(answer);

    public AiCallOutcome OutcomeOfError(HttpStatusCode status, JsonElement? error)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            return AiCallOutcome.AuthenticationFailed;
        }

        return error.Member("error").Text("code") == NoSuchModel ? AiCallOutcome.ModelNotFound : AiCallOutcome.Failed;
    }
}
