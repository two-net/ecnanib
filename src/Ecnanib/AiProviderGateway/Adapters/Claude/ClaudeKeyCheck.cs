using System.Net;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Claude;

/// <summary>
/// Asks Claude whether it takes a key: the list of models, <c>GET /v1/models</c>, with the key in
/// <c>x-api-key</c>. A wrong key is answered with 401.
/// </summary>
internal sealed class ClaudeKeyCheck(KeyCheckCall call) : IAiKeyChecker
{
    // The version of the API that the request is written for. The provider requires one.
    private const string ApiVersion = "2023-06-01";

    public AiProviderCode Provider { get; } = new("claude");

    public Task<KeyVerdict> CheckAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(
            Provider,
            "v1/models?limit=1",
            headers =>
            {
                headers.TryAddWithoutValidation("x-api-key", apiKey);
                headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            },
            KeyCheckCall.Status(HttpStatusCode.Unauthorized),
            cancellationToken);
}
