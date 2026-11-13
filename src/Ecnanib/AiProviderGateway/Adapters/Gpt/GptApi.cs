using System.Net.Http.Headers;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gpt;

/// <summary>What every call to GPT has in common, whether it asks about a key or calls a model.</summary>
internal static class GptApi
{
    public static readonly AiProviderCode Provider = new("gpt");

    /// <summary>Puts the key into the request as a bearer token.</summary>
    public static void Authorize(HttpRequestHeaders headers, string apiKey) =>
        headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
}
