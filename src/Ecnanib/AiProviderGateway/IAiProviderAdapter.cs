using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// The common interface every AI provider sits behind. There is one implementation per provider,
/// under <c>AiProviderGateway/Adapters</c>. Other components never use an adapter directly; they
/// call <see cref="IAiProviderGateway"/>.
/// </summary>
public interface IAiProviderAdapter
{
    /// <summary>The provider this adapter talks to.</summary>
    AiProviderCode Provider { get; }

    /// <summary>Makes one structured call to a model of this provider, once.</summary>
    /// <param name="request">The model, the prompt, and the schema of the result.</param>
    /// <param name="apiKey">
    /// The user's key for this provider. It is passed on its own, not inside the request, so that
    /// it is never part of a value that can be printed or logged. An adapter sends it with this
    /// one call and keeps it nowhere (FR-KEY-9).
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// How the call ended. A provider that refuses, fails, or does not answer is an
    /// <see cref="AiCallOutcome"/>, and not an error: this does not fail for it.
    /// </returns>
    Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken);
}
