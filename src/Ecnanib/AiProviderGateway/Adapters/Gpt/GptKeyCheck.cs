using System.Net;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters.Gpt;

/// <summary>
/// Asks GPT whether it takes a key: the list of models, <c>GET /v1/models</c>, with the key as a
/// bearer token. A wrong key is answered with 401.
/// </summary>
internal sealed class GptKeyCheck(KeyCheckCall call) : IAiKeyChecker
{
    public AiProviderCode Provider { get; } = new("gpt");

    public Task<KeyVerdict> CheckAsync(string apiKey, CancellationToken cancellationToken) =>
        call.AskAsync(
            Provider,
            "v1/models",
            headers => headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}"),
            KeyCheckCall.Status(HttpStatusCode.Unauthorized),
            cancellationToken);
}
