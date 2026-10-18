using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Resilience;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Providers;

/// <summary>xAI (Grok): OpenAI-compatible Responses API plus xAI's richer <c>GET /language-models</c> listing.</summary>
internal sealed class GrokClient : OpenAiResponsesClientBase
{
    public GrokClient(IHttpClientFactory httpClientFactory, IOptions<AiOptions> options, AiRetryPolicy retry, TimeProvider time, ILogger<GrokClient> logger)
        : base(AiProvider.Grok, httpClientFactory, options, retry, time, logger)
    {
    }

    protected override async Task<IReadOnlyList<AiModelInfo>> ListModelsCoreAsync(AiKeyHandle key, CancellationToken ct)
    {
        var list = await SendAsync<XaiLanguageModelList>(key, HttpMethod.Get, "language-models", null, ListOperation, null, ct).ConfigureAwait(false);
        var models = new List<AiModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.Models ?? [])
        {
            var id = item.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                continue;

            var text = item.ArrayContainsString("input_modalities", "text") && item.ArrayContainsString("output_modalities", "text");
            models.Add(new AiModelInfo(Provider, id, id, FromUnixSeconds(item.GetInt64OrNull("created")), null, null, text, item));
        }

        return models;
    }
}
