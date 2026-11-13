using System.Net;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Claude;

/// <summary>
/// Asks Claude whether it takes a key: the list of models, <c>GET /v1/models</c>, with the key in
/// <c>x-api-key</c>. A wrong key is answered with 401.
/// </summary>
internal sealed class ClaudeKeyCheck(KeyCheckCall call) : IAiKeyChecker
{
    public AiProviderCode Provider => ClaudeApi.Provider;

    public Task<KeyVerdict> CheckAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(
            Provider,
            "v1/models?limit=1",
            headers => ClaudeApi.Authorize(headers, apiKey),
            KeyCheckCall.Status(HttpStatusCode.Unauthorized),
            cancellationToken);
}
