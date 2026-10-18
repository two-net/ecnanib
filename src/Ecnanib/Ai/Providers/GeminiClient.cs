using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Resilience;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Providers;

/// <summary>Google (Gemini) over <c>generateContent</c>, with structured output through <c>responseJsonSchema</c>.</summary>
internal sealed class GeminiClient : AiProviderClientBase
{
    private const string ModelPrefix = "models/";

    public GeminiClient(IHttpClientFactory httpClientFactory, IOptions<AiOptions> options, AiRetryPolicy retry, TimeProvider time, ILogger<GeminiClient> logger)
        : base(AiProvider.Gemini, GeminiErrorParser.Instance, httpClientFactory, options, retry, time, logger)
    {
    }

    protected override void ApplyAuth(HttpRequestMessage message, AiKeyHandle key) =>
        message.Headers.TryAddWithoutValidation("x-goog-api-key", key.Credentials.ApiKey);

    protected override async Task<IReadOnlyList<AiModelInfo>> ListModelsCoreAsync(AiKeyHandle key, CancellationToken ct)
    {
        var models = new List<AiModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        for (var page = 1; ; page++)
        {
            var path = "models?pageSize=1000" + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var list = await SendAsync<GeminiModelList>(key, HttpMethod.Get, path, null, ListOperation, null, ct).ConfigureAwait(false);
            foreach (var item in list.Models ?? [])
            {
                var name = item.GetStringOrNull("name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var id = name.StartsWith(ModelPrefix, StringComparison.Ordinal) ? name[ModelPrefix.Length..] : name;
                if (!seen.Add(id))
                    continue;

                models.Add(new AiModelInfo(
                    Provider,
                    id,
                    item.GetStringOrNull("displayName") ?? id,
                    null,
                    item.GetInt32OrNull("inputTokenLimit"),
                    item.GetInt32OrNull("outputTokenLimit"),
                    item.ArrayContainsString("supportedGenerationMethods", "generateContent"),
                    item));
            }

            if (string.IsNullOrEmpty(list.NextPageToken))
                break;
            if (string.Equals(list.NextPageToken, pageToken, StringComparison.Ordinal) || page >= MaxPages)
                throw PaginationLoop(ListOperation);
            pageToken = list.NextPageToken;
        }

        return models;
    }

    protected override async Task<AiCompletion> CompleteCoreAsync(AiKeyHandle key, AiRequest request, int maxOutputTokens, CancellationToken ct)
    {
        var body = new GeminiGenerateRequest(
            string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : new GeminiContent(null, [new GeminiPart(request.SystemPrompt, null)]),
            [new GeminiContent("user", [new GeminiPart(request.UserPrompt, null)])],
            new GeminiGenerationConfig(maxOutputTokens, request.OutputSchema is null ? null : "application/json", request.OutputSchema));

        var path = ModelPrefix + Uri.EscapeDataString(request.Model) + ":generateContent";
        var response = await SendAsync<GeminiGenerateResponse>(key, HttpMethod.Post, path, body, CompleteOperation, request.Model, ct).ConfigureAwait(false);

        string text;
        AiFinishReason finish;
        string? raw;
        var candidate = response.Candidates?.FirstOrDefault();
        if (candidate is null)
        {
            var block = response.PromptFeedback?.BlockReason;
            if (string.IsNullOrEmpty(block))
                throw new AiRequestException($"{Provider} {CompleteOperation} failed: response has no candidates", Provider, AiErrorKind.InvalidResponse, model: request.Model, requestId: response.ResponseId);
            text = string.Empty;
            finish = AiFinishReason.ContentFiltered;
            raw = "blocked:" + block;
        }
        else
        {
            text = string.Concat((candidate.Content?.Parts ?? []).Where(p => p.Thought != true).Select(p => p.Text));
            raw = candidate.FinishReason;
            finish = raw switch
            {
                "STOP" => text.Length > 0 ? AiFinishReason.Stop : AiFinishReason.Other,
                "MAX_TOKENS" => AiFinishReason.MaxTokens,
                "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" => AiFinishReason.ContentFiltered,
                _ => AiFinishReason.Other,
            };
        }

        var usage = response.UsageMetadata is { } u
            ? new AiTokenUsage(u.PromptTokenCount ?? 0, (u.CandidatesTokenCount ?? 0) + (u.ThoughtsTokenCount ?? 0), u.CachedContentTokenCount, u.ThoughtsTokenCount)
            : null;

        return new AiCompletion(
            Provider,
            response.ModelVersion ?? request.Model,
            text,
            request.OutputSchema is not null && finish == AiFinishReason.Stop,
            finish,
            raw,
            UsageOrWarn(usage, request.Model),
            TimeSpan.Zero,
            response.ResponseId);
    }
}
