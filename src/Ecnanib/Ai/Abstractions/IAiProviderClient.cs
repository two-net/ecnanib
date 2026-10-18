namespace Ecnanib.Ai.Abstractions;

/// <summary>
/// One provider's API over the user's own key (AI-01..04). Both operations are idempotent reads; transient failures
/// are retried with backoff until the caller's token is cancelled (FR-AI-13). A handle for another provider is
/// rejected before any request is made (FR-KEY-05).
/// </summary>
public interface IAiProviderClient
{
    AiProvider Provider { get; }

    /// <summary>
    /// Lists the provider's models; also the key-validation call (FR-KEY-03, FR-SYNC-01). An invalid key surfaces as
    /// <see cref="AiAuthException"/>.
    /// </summary>
    Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(AiKeyHandle key, CancellationToken ct);

    /// <summary>
    /// Sends system and user content and returns the text, finish reason and usage (AI-02, AI-04). A rejected model
    /// identifier surfaces as <see cref="AiRequestException"/> with <see cref="AiErrorKind.UnknownModel"/> (FR-SYNC-04).
    /// </summary>
    Task<AiCompletion> CompleteAsync(AiKeyHandle key, AiRequest request, CancellationToken ct);
}
