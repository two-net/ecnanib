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

    /// <summary>Makes one structured call to a model of this provider.</summary>
    /// <param name="request">The model, the prompt, and the schema of the result.</param>
    /// <param name="apiKey">
    /// The user's key for this provider. It is passed on its own, not inside the request, so that
    /// it is never part of a value that can be printed or logged.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AiCallResult> CompleteAsync(AiCallRequest request, string apiKey, CancellationToken cancellationToken);
}
