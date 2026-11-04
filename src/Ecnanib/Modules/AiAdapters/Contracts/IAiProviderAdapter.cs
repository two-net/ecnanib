using System.Globalization;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// What one provider does differently: the shape of its requests, answers and errors. Adding a provider
/// takes a class that implements this, registered in AddAiAdapters, and its section in AiProviders
/// (NFR-MNT-02). Everything else, from the base URL to retries, limits and logging, is shared, and the
/// adapter never sends anything itself. That's why this can be public: nothing here reaches the network.
/// See AI adapters in docs/architecture.md.
/// </summary>
/// <remarks>
/// Every request URI is relative, such as "/v1/messages": the shared pipeline puts the configured base URL
/// in front of it, and refuses an absolute one (NFR-SEC-10). Reveal the key only into a header, never into
/// the URI, which reaches exceptions. Methods that read a body throw when they can't, and the call then
/// fails as <see cref="AiFailure.Other"/>, without the exception's message, which could quote the body.
/// </remarks>
public interface IAiProviderAdapter
{
    /// <summary>
    /// The provider's name, such as "OpenAI": its AiProviders section, the catalog's provider and the
    /// credential's. Letters and digits only. Stored, so never renamed.
    /// </summary>
    string Name { get; }

    /// <summary>A request for one page of the key's models, the first when <paramref name="pageToken"/> is null.</summary>
    HttpRequestMessage ListModels(SecretValue apiKey, string? pageToken);

    /// <summary>Reads a successful model list.</summary>
    AiModelPage ReadModels(byte[] body);

    /// <summary>The provider's request for <paramref name="request"/>.</summary>
    HttpRequestMessage Complete(SecretValue apiKey, AiRequest request);

    /// <summary>Reads a successful answer, including one where the model refused.</summary>
    AiCompletion ReadCompletion(byte[] body);

    /// <summary>
    /// Reads an error response, or returns null when the body says nothing useful. A null
    /// <see cref="AiProviderError.Failure"/> leaves the failure to the status code.
    /// </summary>
    AiProviderError? ReadError(int statusCode, byte[] body);
}

/// <summary>A successful answer as an adapter read it. Its ToString shows no text.</summary>
/// <param name="Usage">The tokens billed for it, counting reasoning or thinking tokens as output.</param>
/// <param name="Truncated">The model stopped at the request's MaxOutputTokens.</param>
/// <param name="Refused">The model declined, such as Anthropic's "refusal" stop reason. The call then fails as <see cref="AiFailure.Refusal"/>, and its tokens still count.</param>
public sealed record AiCompletion(string Text, AiUsage Usage, bool Truncated = false, bool Refused = false)
{
    public override string ToString() =>
        $"{nameof(AiCompletion)}, {Text.Length.ToString(CultureInfo.InvariantCulture)} characters, {Usage}";
}

/// <summary>What a provider's error body says. Its ToString leaves out the message, which can quote the prompt.</summary>
/// <param name="Failure">Where the status code alone would be wrong, such as an overload sent as 400. Null leaves it to the status.</param>
/// <param name="Code">The provider's error type or code, such as "rate_limit_error". It's logged only when it's a plain token.</param>
/// <param name="Message">The provider's explanation, which reaches the decision trace and never a log.</param>
public sealed record AiProviderError(AiFailure? Failure = null, string? Code = null, string? Message = null)
{
    public override string ToString() => $"{nameof(AiProviderError)} {Failure} {Code}";
}
