using System.Net.Http.Headers;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gemini;

/// <summary>What every call to Gemini has in common, whether it asks about a key or calls a model.</summary>
internal static class GeminiApi
{
    public static readonly AiProviderCode Provider = new("gemini");

    private const string WrongKey = "API_KEY_INVALID";

    /// <summary>Puts the key into <c>x-goog-api-key</c>. Gemini also takes it in the address, which is logged, so it never goes there.</summary>
    public static void Authorize(HttpRequestHeaders headers, string apiKey) =>
        headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

    /// <summary>
    /// Whether the error of an answer names the key as what is wrong: the reason
    /// <c>API_KEY_INVALID</c> in <c>error.details[]</c>. Gemini answers a wrong key with 400, as
    /// it answers a wrong request, so only the reason tells the two apart.
    /// </summary>
    public static bool NamesWrongKey(JsonElement? answer) =>
        answer.Member("error") is { } error
        && error.Items("details").Any(detail => detail.Text("reason") == WrongKey);
}
