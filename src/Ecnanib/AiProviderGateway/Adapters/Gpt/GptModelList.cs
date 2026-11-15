using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gpt;

/// <summary>
/// Lists the models of GPT: <c>GET /v1/models</c>, with the key as a bearer token. The list comes
/// whole, in one answer. A wrong key is answered with 401.
/// </summary>
/// <remarks>
/// <para>
/// GPT says when a model was created and, once that is announced, the day it is shut down. It
/// says no name for people and no number of tokens, so those are unknown.
/// </para>
/// <para>
/// A model that an account trained itself is listed to that account, under an id that begins
/// with <c>ft:</c> and names the account. It is not a model of the provider's catalog, and the
/// list may be asked with a user's key, so it is left out: one user's model is not shown to
/// another.
/// </para>
/// </remarks>
internal sealed class GptModelList(ModelListCall call) : IAiModelLister, IModelListFormat
{
    // How the id of a model begins that an account trained itself.
    private const string TrainedByAnAccount = "ft:";

    public AiProviderCode Provider => GptApi.Provider;

    public Task<AiModelList> ListAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, apiKey, cancellationToken);

    public string PathOf(string? next) => "v1/models";

    public void Authorize(HttpRequestHeaders headers, string apiKey) => GptApi.Authorize(headers, apiKey);

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

            if (id.StartsWith(TrainedByAnAccount, StringComparison.Ordinal))
            {
                continue;
            }

            models.Add(new ListedAiModel(
                id,
                DisplayName: null,
                ContextTokens: null,
                OutputTokens: null,
                model.Moment("created"),
                DeprecatedAt: null,
                model.Moment("shutdown_date")));
        }

        return new ModelListPage(models, Next: null);
    }

    public bool RefusesKey(HttpStatusCode status, JsonElement? error) => status == HttpStatusCode.Unauthorized;
}
