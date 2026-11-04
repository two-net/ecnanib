namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// Calls a model with a user's key, which spends their money. Only AiCouncil may use this, inside an
/// agent's budget (FR-CYC-07), and BannedApis enforces that. The same call works for every provider, so the
/// council never knows which one it's talking to (EXT-AI-05). See AI adapters in docs/architecture.md.
/// </summary>
public interface IAiCompletions
{
    /// <summary>
    /// Sends <paramref name="request"/>, waiting for a free slot on the key's credential first, and retrying
    /// per AiProviders:&lt;Name&gt; (EXT-AI-03). Each attempt is logged under AiCouncil (FR-LOG-07).
    /// </summary>
    /// <exception cref="ArgumentException">No adapter has the key's provider name.</exception>
    /// <exception cref="AiException">It failed after its retries. Use the fallback model (FR-ROLE-06).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired, which is never retried.</exception>
    Task<AiResponse> CompleteAsync(AiKey key, AiRequest request, CancellationToken cancellationToken);
}
