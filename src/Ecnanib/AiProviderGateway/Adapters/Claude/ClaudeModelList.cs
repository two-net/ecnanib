using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Claude;

/// <summary>
/// Lists the models of Claude: <c>GET /v1/models</c>, with the key in <c>x-api-key</c>. The list
/// comes in pages: <c>has_more</c> says that there is another, and <c>last_id</c> is what the
/// next one is asked after. A wrong key is answered with 401.
/// </summary>
/// <remarks>
/// Claude says the most about a model: its name, when it was released, how many tokens it takes
/// and writes, and when it was deprecated and is retired. A model that is retired is no longer
/// listed.
/// </remarks>
internal sealed class ClaudeModelList(ModelListCall call) : IAiModelLister, IModelListFormat
{
    // The most models Claude sends in one page.
    private const int PageSize = 1000;

    public AiProviderCode Provider => ClaudeApi.Provider;

    public Task<AiModelList> ListAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, apiKey, cancellationToken);

    public string PathOf(string? next) =>
        next is null ? $"v1/models?limit={PageSize}" : $"v1/models?limit={PageSize}&after_id={Uri.EscapeDataString(next)}";

    public void Authorize(HttpRequestHeaders headers, string apiKey) => ClaudeApi.Authorize(headers, apiKey);

    public ModelListPage? Read(JsonElement answer)
    {
        if (answer.Member("data") is not { ValueKind: JsonValueKind.Array } data)
        {
            return null;
        }

        var models = new List<ListedAiModel>();
        foreach (var model in data.EnumerateArray())
        {
            if (string.IsNullOrWhiteSpace(model.Text("id")))
            {
                return null;
            }

            models.Add(new ListedAiModel(
                model.Text("id")!,
                model.Text("display_name"),
                model.Tokens("max_input_tokens"),
                model.Tokens("max_tokens"),
                model.Moment("created_at"),
                model.Moment("deprecated_at"),
                model.Moment("retires_at")));
        }

        if (answer.Member("has_more") is not { ValueKind: JsonValueKind.True })
        {
            return new ModelListPage(models, Next: null);
        }

        // More to come, and nothing to ask for it after: not a page of the list.
        return answer.Text("last_id") is { Length: > 0 } last ? new ModelListPage(models, last) : null;
    }

    public bool RefusesKey(HttpStatusCode status, JsonElement? error) => status == HttpStatusCode.Unauthorized;
}
