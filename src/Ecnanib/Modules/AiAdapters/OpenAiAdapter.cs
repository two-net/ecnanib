using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>
/// GPT, through the Responses API, which OpenAI's newer models need for tools. See The four providers in
/// docs/architecture.md.
/// </summary>
internal sealed class OpenAiAdapter : IAiProviderAdapter
{
    private static readonly ResponsesDialect Dialect = new(["web_search"], HideInlineCitations: false, ReasoningCountedApart: false);

    /// <summary>
    /// Families in the model list that don't write text, or not through the Responses API. OpenAI's list
    /// says nothing about what a model does, so names are all there is.
    /// </summary>
    private static readonly string[] NonTextModels =
        ["embedding", "tts", "whisper", "dall-e", "gpt-image", "moderation", "realtime", "audio", "transcribe", "sora", "babbage", "davinci"];

    /// <summary>
    /// Error codes for a key with no money left, which come as 429 like a rate limit, but won't clear by
    /// waiting. So they're Other, which isn't retried.
    /// </summary>
    private static readonly string[] OutOfMoney =
        ["insufficient_quota", "credit_balance_exhausted", "organization_spend_limit_exceeded", "project_spend_limit_exceeded", "organization_usage_limit_exceeded"];

    public string Name => "OpenAI";

    /// <summary>The whole list, which OpenAI doesn't page.</summary>
    public HttpRequestMessage ListModels(SecretValue apiKey, string? pageToken) =>
        ResponsesFormat.Bearer(AdapterJson.Get("/v1/models"), apiKey);

    public AiModelPage ReadModels(byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var models = document.RootElement.Required("data").EnumerateArray()
            .Select(model => new AiModel(model.String("id")!, CreatedAt: model.UnixSeconds("created")))
            .Where(model => !NonTextModels.Any(family => model.Id.Contains(family, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new AiModelPage(models);
    }

    public HttpRequestMessage Complete(SecretValue apiKey, AiRequest request) => ResponsesFormat.Complete(apiKey, request, Dialect);

    public AiCompletion ReadCompletion(byte[] body) => ResponsesFormat.ReadCompletion(body, Dialect);

    public AiProviderError? ReadError(int statusCode, byte[] body)
    {
        using var document = AdapterJson.Parse(body);

        if (document.RootElement.Object("error") is not { } error)
        {
            return null;
        }

        var code = error.String("code");
        var type = error.String("type");
        var broke = OutOfMoney.Any(value => value == code || value == type);

        return new AiProviderError(broke ? AiFailure.Other : null, code ?? type, error.String("message"));
    }
}
