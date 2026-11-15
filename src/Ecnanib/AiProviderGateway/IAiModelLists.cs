using Ecnanib.Platform.Logging;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// Asks an AI provider for the list of its models, which is what the catalog is filled from
/// (FR-CAT-1). Callers name the provider by code and never branch on it. See docs/ai-providers.md.
/// </summary>
public interface IAiModelLists
{
    /// <summary>The providers whose models can be listed.</summary>
    IReadOnlyCollection<AiProviderCode> Providers { get; }

    /// <summary>Asks <paramref name="provider"/> for its models.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="apiKey">
    /// The key the list is asked with: the service's own for the catalog, or a user's. It is sent
    /// to the provider in a header and is kept nowhere: not in a log event, and not in an error.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The whole list, or how asking ended. A provider that does not answer is an outcome: this
    /// does not fail for it.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The provider's models cannot be listed, or the configuration file has no entry for it.
    /// </exception>
    Task<AiModelList> ListAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken);
}

/// <summary>What asking a provider for its models gave.</summary>
/// <param name="Outcome">How asking ended.</param>
/// <param name="Models">
/// Every model the provider listed, each once, when the outcome is
/// <see cref="AiModelListOutcome.Listed"/>, and none otherwise. A list is whole or it is not a
/// list: a part of one would make the models of the rest look gone.
/// </param>
public sealed record AiModelList(AiModelListOutcome Outcome, IReadOnlyList<ListedAiModel> Models);

/// <summary>How asking a provider for its models ended.</summary>
public enum AiModelListOutcome
{
    /// <summary>The provider listed its models.</summary>
    Listed,

    /// <summary>The provider answered as it answers a wrong key.</summary>
    AuthenticationFailed,

    /// <summary>The list took longer than its time limit.</summary>
    TimedOut,

    /// <summary>
    /// Anything else: no connection, a limit on requests, a fault of the provider, or an answer
    /// that is not its list of models, an empty one included.
    /// </summary>
    Failed,
}

/// <summary>
/// A model as its provider lists it. What the provider does not say is null, which is unknown:
/// nothing is guessed (FR-CAT-4).
/// </summary>
/// <param name="Id">The provider's identifier of the model, which a call to the model names it by.</param>
/// <param name="DisplayName">The name the provider gives the model for people to read.</param>
/// <param name="ContextTokens">The most tokens the model takes as input.</param>
/// <param name="OutputTokens">The most tokens the model writes in one answer.</param>
/// <param name="ReleasedAt">When the provider released the model.</param>
/// <param name="DeprecatedAt">When the provider deprecated the model.</param>
/// <param name="RetiresAt">When the provider plans to stop serving the model.</param>
public sealed record ListedAiModel(
    string Id,
    string? DisplayName,
    int? ContextTokens,
    int? OutputTokens,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? DeprecatedAt,
    DateTimeOffset? RetiresAt);

/// <summary>
/// The list of one provider's models. There is one implementation per provider, under
/// <c>AiProviderGateway/Adapters</c>, beside the provider's <see cref="IAiProviderAdapter"/>.
/// Other components never use one directly; they call <see cref="IAiModelLists"/>.
/// </summary>
internal interface IAiModelLister
{
    /// <summary>The provider this lists the models of.</summary>
    AiProviderCode Provider { get; }

    /// <summary>Asks the provider for its models.</summary>
    Task<AiModelList> ListAsync(string apiKey, CancellationToken cancellationToken);
}

/// <summary>Sends each request for a list to the lister registered for its provider.</summary>
internal sealed class AiModelListRouter : IAiModelLists
{
    private readonly Dictionary<AiProviderCode, IAiModelLister> _listers = [];
    private readonly ILogger<AiModelListRouter> _logger;

    public AiModelListRouter(IEnumerable<IAiModelLister> listers, ILogger<AiModelListRouter> logger)
    {
        _logger = logger;

        foreach (var lister in listers)
        {
            if (!_listers.TryAdd(lister.Provider, lister))
            {
                throw new InvalidOperationException(
                    $"More than one list of models is registered for AI provider '{lister.Provider}'.");
            }
        }
    }

    public IReadOnlyCollection<AiProviderCode> Providers => _listers.Keys;

    public async Task<AiModelList> ListAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken)
    {
        // Said without what was given: a key is in no error.
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (!_listers.TryGetValue(provider, out var lister))
        {
            throw new InvalidOperationException($"No list of models is registered for AI provider '{provider}'.");
        }

        // The HTTP calls that the list makes are logged with the provider. The key is never given
        // to the logger.
        using var call = _logger.BeginCorrelation(new Correlation(Provider: provider.Value));

        return await lister.ListAsync(apiKey, cancellationToken);
    }
}
