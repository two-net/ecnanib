using System.Net;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Grok;

/// <summary>
/// Asks Grok whether it takes a key: the list of models, <c>GET /v1/models</c>, with the key as a
/// bearer token. A wrong key is answered with 400, and not with 401 as the other providers do.
/// The request has no parameter that could be wrong, so a 400 is about the key.
/// </summary>
internal sealed class GrokKeyCheck(KeyCheckCall call) : IAiKeyChecker
{
    public AiProviderCode Provider => GrokApi.Provider;

    public Task<KeyVerdict> CheckAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(
            Provider,
            "v1/models",
            headers => GrokApi.Authorize(headers, apiKey),
            KeyCheckCall.Status(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized),
            cancellationToken);
}
