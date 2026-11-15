using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Grok;

/// <summary>
/// Lists the models of Grok: <c>GET /v1/models</c>, with the key as a bearer token. The list comes
/// whole, in one answer, and holds the models that the key may use. A wrong key is answered with
/// 400, and not with 401 as the other providers do: the request has no parameter that could be
/// wrong, so a 400 is about the key.
/// </summary>
/// <remarks>
/// Grok says when a model was created and how many tokens it takes. It says no name for people,
/// no number of tokens that it writes, and no date of an end, so those are unknown.
/// </remarks>
internal sealed class GrokModelList(ModelListCall call) : IAiModelLister, IModelListFormat
{
    public AiProviderCode Provider => GrokApi.Provider;

    public Task<AiModelList> ListAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, apiKey, cancellationToken);

    public string PathOf(string? next) => "v1/models";

    public void Authorize(HttpRequestHeaders headers, string apiKey) => GrokApi.Authorize(headers, apiKey);

    public ModelListPage? Read(JsonElement answer)
    {
        if (answer.Member("data") is not { ValueKind: JsonValueKind.Array } data)
        {
            return null;
        }

        var models = new List<ListedAiModel>();
        foreach (var model in data.EnumerateArray())
        {
            if (model.Text("id") is not { } id || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            models.Add(new ListedAiModel(
                id,
                DisplayName: null,
                model.Tokens("context_length"),
                OutputTokens: null,
                model.Moment("created"),
                DeprecatedAt: null,
                RetiresAt: null));
        }

        return new ModelListPage(models, Next: null);
    }

    public bool RefusesKey(HttpStatusCode status, JsonElement? error) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized;
}
