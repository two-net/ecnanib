using System.Net.Http.Headers;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Claude;

/// <summary>What every call to Claude has in common, whether it asks about a key or calls a model.</summary>
internal static class ClaudeApi
{
    public static readonly AiProviderCode Provider = new("claude");

    // The version of the API that the requests are written for. The provider requires one.
    private const string Version = "2023-06-01";

    /// <summary>Puts the key into <c>x-api-key</c>, beside the version of the API.</summary>
    public static void Authorize(HttpRequestHeaders headers, string apiKey)
    {
        headers.TryAddWithoutValidation("x-api-key", apiKey);
        headers.TryAddWithoutValidation("anthropic-version", Version);
    }
}
