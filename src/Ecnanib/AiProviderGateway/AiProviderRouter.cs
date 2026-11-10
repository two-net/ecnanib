using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>Routes each call to the adapter registered for its provider.</summary>
internal sealed class AiProviderRouter : IAiProviderGateway
{
    private readonly Dictionary<AiProviderCode, IAiProviderAdapter> _adapters = [];

    public AiProviderRouter(IEnumerable<IAiProviderAdapter> adapters)
    {
        foreach (var adapter in adapters)
        {
            if (!_adapters.TryAdd(adapter.Provider, adapter))
            {
                throw new InvalidOperationException(
                    $"More than one adapter is registered for AI provider '{adapter.Provider}'.");
            }
        }
    }

    public IReadOnlyCollection<AiProviderCode> Providers => _adapters.Keys;

    public Task<AiCallResult> CompleteAsync(
        AiProviderCode provider,
        AiCallRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(provider, out var adapter))
        {
            throw new InvalidOperationException($"No adapter is registered for AI provider '{provider}'.");
        }

        return adapter.CompleteAsync(request, apiKey, cancellationToken);
    }
}
