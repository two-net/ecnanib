using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Resilience;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Providers;

/// <summary>Anthropic (Claude) over the Messages API with native structured output (<c>output_config.format</c>).</summary>
internal sealed class ClaudeClient : AiProviderClientBase
{
    public ClaudeClient(IHttpClientFactory httpClientFactory, IOptions<AiOptions> options, AiRetryPolicy retry, TimeProvider time, ILogger<ClaudeClient> logger)
        : base(AiProvider.Claude, ClaudeErrorParser.Instance, httpClientFactory, options, retry, time, logger)
    {
    }

    protected override void ApplyAuth(HttpRequestMessage message, AiKeyHandle key)
    {
        message.Headers.TryAddWithoutValidation("x-api-key", key.Credentials.ApiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", Endpoint.ApiVersion);
    }

    protected override async Task<IReadOnlyList<AiModelInfo>> ListModelsCoreAsync(AiKeyHandle key, CancellationToken ct)
    {
        var models = new List<AiModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? afterId = null;
        for (var page = 1; ; page++)
        {
            var path = "models?limit=1000" + (afterId is null ? string.Empty : "&after_id=" + Uri.EscapeDataString(afterId));
            var list = await SendAsync<ClaudeModelList>(key, HttpMethod.Get, path, null, ListOperation, null, ct).ConfigureAwait(false);
            foreach (var item in list.Data ?? [])
            {
                var id = item.GetStringOrNull("id");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                    continue;

                models.Add(new AiModelInfo(
                    Provider,
                    id,
                    item.GetStringOrNull("display_name") ?? id,
                    ParseTimestamp(item.GetStringOrNull("created_at")),
                    item.GetInt32OrNull("max_input_tokens"),
                    item.GetInt32OrNull("max_tokens"),
                    SupportsTextGeneration: true,
                    item));
            }

            if (list.HasMore != true || string.IsNullOrEmpty(list.LastId))
                break;
            if (string.Equals(list.LastId, afterId, StringComparison.Ordinal) || page >= MaxPages)
                throw PaginationLoop(ListOperation);
            afterId = list.LastId;
        }

        return models;
    }

    protected override async Task<AiCompletion> CompleteCoreAsync(AiKeyHandle key, AiRequest request, int maxOutputTokens, CancellationToken ct)
    {
        var body = new ClaudeMessageRequest(
            request.Model,
            maxOutputTokens,
            string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
            [new ClaudeMessage("user", request.UserPrompt)],
            request.OutputSchema is { } schema ? new ClaudeOutputConfig(new ClaudeOutputFormat("json_schema", schema)) : null);

        var response = await SendAsync<ClaudeMessageResponse>(key, HttpMethod.Post, "messages", body, CompleteOperation, request.Model, ct).ConfigureAwait(false);

        var text = string.Concat((response.Content ?? []).Where(b => b.Type == "text").Select(b => b.Text));
        var finish = response.StopReason switch
        {
            "end_turn" or "stop_sequence" => text.Length > 0 ? AiFinishReason.Stop : AiFinishReason.Other,
            "max_tokens" => AiFinishReason.MaxTokens,
            "refusal" => AiFinishReason.Refusal,
            _ => AiFinishReason.Other,
        };
        var usage = response.Usage is { } u
            ? new AiTokenUsage(
                (u.InputTokens ?? 0) + (u.CacheCreationInputTokens ?? 0) + (u.CacheReadInputTokens ?? 0),
                u.OutputTokens ?? 0,
                u.CacheReadInputTokens,
                null)
            : null;

        return new AiCompletion(
            Provider,
            response.Model ?? request.Model,
            text,
            request.OutputSchema is not null && finish == AiFinishReason.Stop,
            finish,
            response.StopReason,
            UsageOrWarn(usage, request.Model),
            TimeSpan.Zero,
            response.Id);
    }
}
