using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// What the rest of the application uses to call a model. Callers name the provider by code and
/// never branch on it, so adding a provider adapter changes nothing outside this component. See
/// docs/ai-providers.md.
/// </summary>
public interface IAiProviderGateway
{
    /// <summary>The providers that have an adapter.</summary>
    IReadOnlyCollection<AiProviderCode> Providers { get; }

    /// <summary>
    /// Makes one structured call through the adapter for <paramref name="provider"/>, once, and
    /// logs it with its duration, outcome, and token usage (NFR-LOG-5).
    /// </summary>
    /// <param name="provider">The provider of the model.</param>
    /// <param name="request">The model, the prompt, and the schema of the result.</param>
    /// <param name="apiKey">
    /// The user's key for the provider. The gateway takes it with each call and never fetches or
    /// keeps one: what decrypts a stored key hands it over for the one call (FR-KEY-9).
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// How the call ended, which is the caller's to act on: a call that timed out or gave no
    /// usable result may be made again (FR-TEAM-9), one that failed authentication has the key
    /// checked again (FR-KEY-3), and a model that the provider does not have needs its user
    /// (FR-CAT-8). A provider that does not answer is one of the outcomes: this does not fail
    /// for it.
    /// </returns>
    /// <exception cref="InvalidOperationException">No adapter exists for the provider, or the configuration file has no entry for it.</exception>
    /// <exception cref="ArgumentException">The model, the prompt, or the key is empty, or the schema is not a JSON object.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    Task<AiCallResult> CompleteAsync(
        AiProviderCode provider,
        AiCallRequest request,
        string apiKey,
        CancellationToken cancellationToken);
}
