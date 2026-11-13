using System.Net;
using System.Text.Json;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gemini;

/// <summary>
/// Asks Gemini whether it takes a key: the list of models, <c>GET /v1beta/models</c>, with the
/// key in <c>x-goog-api-key</c>. A wrong key is answered with 400 and the reason
/// <c>API_KEY_INVALID</c>. A 400 without that reason is about something else than the key.
/// </summary>
internal sealed class GeminiKeyCheck(KeyCheckCall call) : IAiKeyChecker
{
    private const string WrongKey = "API_KEY_INVALID";

    public AiProviderCode Provider { get; } = new("gemini");

    public Task<KeyVerdict> CheckAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(
            Provider,
            "v1beta/models?pageSize=1",
            headers => headers.TryAddWithoutValidation("x-goog-api-key", apiKey),
            RefusesAsync,
            cancellationToken);

    private static async Task<bool> RefusesAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => true,
            HttpStatusCode.BadRequest => await SaysWrongKeyAsync(response, cancellationToken),
            _ => false,
        };

    /// <summary>Whether the error of an answer names the key as what is wrong: <c>error.details[].reason</c>.</summary>
    private static async Task<bool> SaysWrongKeyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var answer = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            return answer.RootElement.ValueKind == JsonValueKind.Object
                && answer.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Array
                && details.EnumerateArray().Any(detail =>
                    detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String
                    && reason.GetString() == WrongKey);
        }
        catch (JsonException)
        {
            // Not the provider's error, so not its answer to a wrong key.
            return false;
        }
    }
}
