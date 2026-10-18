using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Resilience;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Providers;

/// <summary>OpenAI (GPT): Responses API plus <c>GET /models</c>, which reports id, creation time and owner only.</summary>
internal sealed class GptClient : OpenAiResponsesClientBase
{
    public GptClient(IHttpClientFactory httpClientFactory, IOptions<AiOptions> options, AiRetryPolicy retry, TimeProvider time, ILogger<GptClient> logger)
        : base(AiProvider.Gpt, httpClientFactory, options, retry, time, logger)
    {
    }

    protected override async Task<IReadOnlyList<AiModelInfo>> ListModelsCoreAsync(AiKeyHandle key, CancellationToken ct)
    {
        var list = await SendAsync<OpenAiModelList>(key, HttpMethod.Get, "models", null, ListOperation, null, ct).ConfigureAwait(false);
        var models = new List<AiModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.Data ?? [])
        {
            var id = item.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                continue;

            models.Add(new AiModelInfo(Provider, id, id, FromUnixSeconds(item.GetInt64OrNull("created")), null, null, null, item));
        }

        return models;
    }
}
