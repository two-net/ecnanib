using Ecnanib.Ai.Abstractions;
using Ecnanib.Ai.Configuration;
using Ecnanib.Ai.Resilience;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ai.Providers;

/// <summary>
/// Completion over the OpenAI Responses API, shared by GPT and Grok (xAI serves the same shape). Structured output
/// uses <c>text.format</c> with a strict JSON schema; responses are not stored at the provider (<c>store: false</c>).
/// </summary>
internal abstract class OpenAiResponsesClientBase : AiProviderClientBase
{
    private const string SchemaName = "ecnanib_output";

    protected OpenAiResponsesClientBase(AiProvider provider, IHttpClientFactory httpClientFactory, IOptions<AiOptions> options, AiRetryPolicy retry, TimeProvider time, ILogger logger)
        : base(provider, OpenAiErrorParser.Instance, httpClientFactory, options, retry, time, logger)
    {
    }

    protected override void ApplyAuth(HttpRequestMessage message, AiKeyHandle key) =>
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Credentials.ApiKey);

    protected override async Task<AiCompletion> CompleteCoreAsync(AiKeyHandle key, AiRequest request, int maxOutputTokens, CancellationToken ct)
    {
        var body = new ResponsesRequest(
            request.Model,
            string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
            request.UserPrompt,
            maxOutputTokens,
            Store: false,
            request.OutputSchema is { } schema ? new ResponsesTextOptions(new ResponsesTextFormat("json_schema", SchemaName, schema, Strict: true)) : null);

        var response = await SendAsync<ResponsesResponse>(key, HttpMethod.Post, "responses", body, CompleteOperation, request.Model, ct).ConfigureAwait(false);

        var status = response.Status ?? string.Empty;
        if (status == "failed")
        {
            var error = new ProviderError(response.Error?.Code, key.Credentials.Scrub(response.Error?.Message));
            throw AiErrorTranslator.TranslateInBand(Provider, ErrorParser, CompleteOperation, request.Model, error, null, Time.GetUtcNow());
        }

        if (status is "in_progress" or "queued" or "cancelled")
            throw new AiRequestException($"{Provider} {CompleteOperation} failed: response status is '{status}'", Provider, AiErrorKind.InvalidResponse, model: request.Model, requestId: response.Id);

        var parts = (response.Output ?? []).Where(i => i.Type == "message").SelectMany(i => i.Content ?? []).ToList();
        var refusal = parts.FirstOrDefault(p => p.Type == "refusal");
        var text = string.Concat(parts.Where(p => p.Type == "output_text").Select(p => p.Text));

        AiFinishReason finish;
        string raw;
        if (refusal is not null)
        {
            finish = AiFinishReason.Refusal;
            raw = "refusal";
            text = refusal.Refusal ?? text;
        }
        else if (status == "incomplete")
        {
            var reason = response.IncompleteDetails?.Reason;
            finish = reason switch
            {
                "max_output_tokens" => AiFinishReason.MaxTokens,
                "content_filter" => AiFinishReason.ContentFiltered,
                _ => AiFinishReason.Other,
            };
            raw = reason is null ? status : $"{status}:{reason}";
        }
        else if (status == "completed")
        {
            finish = text.Length > 0 ? AiFinishReason.Stop : AiFinishReason.Other;
            raw = status;
        }
        else
        {
            finish = AiFinishReason.Other;
            raw = status;
        }

        var usage = response.Usage is { } u
            ? new AiTokenUsage(u.InputTokens ?? 0, u.OutputTokens ?? 0, u.InputTokensDetails?.CachedTokens, u.OutputTokensDetails?.ReasoningTokens)
            : null;

        return new AiCompletion(
            Provider,
            response.Model ?? request.Model,
            text,
            request.OutputSchema is not null && finish == AiFinishReason.Stop,
            finish,
            raw,
            UsageOrWarn(usage, request.Model),
            TimeSpan.Zero,
            response.Id);
    }
}
