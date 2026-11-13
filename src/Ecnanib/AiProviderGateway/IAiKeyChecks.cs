using Ecnanib.Platform.Logging;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway;

/// <summary>
/// Asks an AI provider whether it takes a key, with a call that only reads: the list of its
/// models (FR-KEY-3). Callers name the provider by code and never branch on it.
/// </summary>
public interface IAiKeyChecks
{
    /// <summary>Asks <paramref name="provider"/> about a key.</summary>
    /// <param name="provider">The provider the key is for.</param>
    /// <param name="apiKey">
    /// The user's key. It is sent to the provider in a header and is kept nowhere: not in a log
    /// event, and not in an error.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// What the provider said. A provider that does not answer is <see cref="KeyVerdict.Unreachable"/>:
    /// this does not fail for it.
    /// </returns>
    /// <exception cref="InvalidOperationException">No check exists for the provider.</exception>
    Task<KeyVerdict> CheckAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// The check of one provider's keys. There is one implementation per provider, under
/// <c>AiProviderGateway/Adapters</c>, beside the provider's <see cref="IAiProviderAdapter"/>.
/// Other components never use one directly; they call <see cref="IAiKeyChecks"/>.
/// </summary>
internal interface IAiKeyChecker
{
    /// <summary>The provider this check asks.</summary>
    AiProviderCode Provider { get; }

    /// <summary>Asks the provider about a key.</summary>
    Task<KeyVerdict> CheckAsync(string apiKey, CancellationToken cancellationToken);
}

/// <summary>Sends each check to the one registered for its provider.</summary>
internal sealed class AiKeyCheckRouter : IAiKeyChecks
{
    private readonly Dictionary<AiProviderCode, IAiKeyChecker> _checkers = [];
    private readonly ILogger<AiKeyCheckRouter> _logger;

    public AiKeyCheckRouter(IEnumerable<IAiKeyChecker> checkers, ILogger<AiKeyCheckRouter> logger)
    {
        _logger = logger;

        foreach (var checker in checkers)
        {
            if (!_checkers.TryAdd(checker.Provider, checker))
            {
                throw new InvalidOperationException(
                    $"More than one key check is registered for AI provider '{checker.Provider}'.");
            }
        }
    }

    public async Task<KeyVerdict> CheckAsync(AiProviderCode provider, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        if (!_checkers.TryGetValue(provider, out var checker))
        {
            throw new InvalidOperationException($"No key check is registered for AI provider '{provider}'.");
        }

        // The HTTP call that the check makes is logged with the provider. The key is never given
        // to the logger.
        using var call = _logger.BeginCorrelation(new Correlation(Provider: provider.Value));

        return await checker.CheckAsync(apiKey, cancellationToken);
    }
}
