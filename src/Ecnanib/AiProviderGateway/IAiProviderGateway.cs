using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// What the rest of the application uses to call a model. Callers name the provider by code and
/// never branch on it, so adding a provider adapter changes nothing outside this component.
/// </summary>
public interface IAiProviderGateway
{
    /// <summary>The providers that have an adapter.</summary>
    IReadOnlyCollection<AiProviderCode> Providers { get; }

    /// <summary>Makes one structured call through the adapter for <paramref name="provider"/>.</summary>
    /// <exception cref="InvalidOperationException">No adapter exists for the provider.</exception>
    Task<AiCallResult> CompleteAsync(
        AiProviderCode provider,
        AiCallRequest request,
        string apiKey,
        CancellationToken cancellationToken);
}
