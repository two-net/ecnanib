using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>
/// Grok, through xAI's Responses API, which is the only way to its web and X search since Live Search was
/// removed. See The four providers in docs/architecture.md.
/// </summary>
internal sealed class XaiAdapter : IAiProviderAdapter
{
    private static readonly ResponsesDialect Dialect = new(["web_search", "x_search"], HideInlineCitations: true, ReasoningCountedApart: true);

    public string Name => "xAI";

    /// <summary>The language models, which xAI doesn't page. Its other list also has image generators.</summary>
    public HttpRequestMessage ListModels(SecretValue apiKey, string? pageToken) =>
        ResponsesFormat.Bearer(AdapterJson.Get("/v1/language-models"), apiKey);

    public AiModelPage ReadModels(byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var models = document.RootElement.Required("models").EnumerateArray()
            .Where(model => !model.TryGetProperty("output_modalities", out _)
                || model.Strings("output_modalities").Contains("text", StringComparer.Ordinal))
            .Select(model => new AiModel(model.String("id")!, CreatedAt: model.UnixSeconds("created")))
            .ToList();

        return new AiModelPage(models);
    }

    public HttpRequestMessage Complete(SecretValue apiKey, AiRequest request) => ResponsesFormat.Complete(apiKey, request, Dialect);

    public AiCompletion ReadCompletion(byte[] body) => ResponsesFormat.ReadCompletion(body, Dialect);

    /// <remarks>
    /// xAI's errors are flat, {"code", "error"}, and its code is a sentence rather than a token. A wrong key
    /// comes back as 400 with no code of its own, so only its message tells it from an invalid request.
    /// </remarks>
    public AiProviderError? ReadError(int statusCode, byte[] body)
    {
        using var document = AdapterJson.Parse(body);
        var root = document.RootElement;
        string? message;
        string? code;

        // Flat as documented, or nested as OpenAI's are, in case an endpoint follows OpenAI more closely.
        if (root.Object("error") is { } error)
        {
            message = error.String("message");
            code = error.String("code") ?? error.String("type");
        }
        else
        {
            message = root.String("error");
            code = root.String("code");
        }

        if (message is null && code is null)
        {
            return null;
        }

        var wrongKey = statusCode == 400 && message?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true;

        return new AiProviderError(wrongKey ? AiFailure.Authentication : null, code, message);
    }
}
