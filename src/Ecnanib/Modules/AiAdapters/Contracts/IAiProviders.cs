namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// The providers that have an adapter, and the models a key can use. Credentials (#20) validates a key by
/// listing its models (FR-KEY-04), and Catalog (#21) syncs from the same list (FR-CAT-02). Calling a model
/// is <see cref="IAiCompletions"/>, which only AiCouncil may use. See AI adapters in docs/architecture.md.
/// </summary>
public interface IAiProviders
{
    /// <summary>Every provider with an adapter, in the order they were registered.</summary>
    IReadOnlyList<AiProviderInfo> All { get; }

    /// <summary>
    /// Every model <paramref name="key"/> can use, following the provider's pages. Each page is retried like
    /// any other call.
    /// </summary>
    /// <exception cref="ArgumentException">No adapter has the key's provider name.</exception>
    /// <exception cref="AiException">
    /// It failed. An invalid key is <see cref="AiFailure.Authentication"/>. A list longer than 20 pages is
    /// <see cref="AiFailure.Other"/>, because a partial list would wrongly retire models (FR-CAT-04).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    Task<IReadOnlyList<AiModel>> ListModelsAsync(AiKey key, CancellationToken cancellationToken);
}
