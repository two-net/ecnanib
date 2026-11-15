using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gemini;

/// <summary>
/// Lists the models of Gemini: <c>GET /v1beta/models</c>, with the key in <c>x-goog-api-key</c>.
/// The list comes in pages: <c>nextPageToken</c> is what the next one is asked with, and is left
/// out of the last. A wrong key is answered with 400 and the reason <c>API_KEY_INVALID</c>.
/// </summary>
/// <remarks>
/// Gemini names a model <c>models/</c> and its id. The catalog keeps the id, which is what the
/// provider's own documentation calls the model. Gemini says the name of a model and how many
/// tokens it takes and writes, and no date.
/// </remarks>
internal sealed class GeminiModelList(ModelListCall call) : IAiModelLister, IModelListFormat
{
    // The most models Gemini sends in one page.
    private const int PageSize = 1000;

    // What the list puts before the id of a model.
    private const string ListPrefix = "models/";

    public AiProviderCode Provider => GeminiApi.Provider;

    public Task<AiModelList> ListAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(this, apiKey, cancellationToken);

    public string PathOf(string? next) =>
        next is null
            ? $"v1beta/models?pageSize={PageSize}"
            : $"v1beta/models?pageSize={PageSize}&pageToken={Uri.EscapeDataString(next)}";

    public void Authorize(HttpRequestHeaders headers, string apiKey) => GeminiApi.Authorize(headers, apiKey);

    public ModelListPage? Read(JsonElement answer)
    {
        var models = new List<ListedAiModel>();

        // Gemini leaves a list out when it is empty, so a page without one is a page of no models.
        if (answer.Member("models") is { } listed)
        {
            if (listed.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var model in listed.EnumerateArray())
            {
                var name = model.Text("name");
                var id = name?.StartsWith(ListPrefix, StringComparison.Ordinal) == true ? name[ListPrefix.Length..] : name;

                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                models.Add(new ListedAiModel(
                    id,
                    model.Text("displayName"),
                    model.Tokens("inputTokenLimit"),
                    model.Tokens("outputTokenLimit"),
                    ReleasedAt: null,
                    DeprecatedAt: null,
                    RetiresAt: null));
            }
        }

        return new ModelListPage(models, answer.Text("nextPageToken") is { Length: > 0 } token ? token : null);
    }

    public bool RefusesKey(HttpStatusCode status, JsonElement? error) =>
        status == HttpStatusCode.Unauthorized || (status == HttpStatusCode.BadRequest && GeminiApi.NamesWrongKey(error));
}
