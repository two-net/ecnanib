using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Keys.Abstractions;

/// <summary>
/// The user's keys (FR-KEY-01..09): add, replace, validate and delete Binance key pairs and one key per AI provider.
/// Every operation is scoped to <c>userId</c> (NFR-SEC-03). A key is validated with its own service before it is
/// stored and is stored only when that succeeds; a refused or unverifiable key is a result, not a row.
/// </summary>
public interface IKeyService
{
    Task<KeyOverview> GetOverviewAsync(Guid userId, CancellationToken cancellationToken);

    Task<ExchangeKeyView?> GetExchangeKeyAsync(Guid userId, Guid keyId, CancellationToken cancellationToken);

    Task<ProviderKeySlot> GetProviderSlotAsync(Guid userId, AiProvider provider, CancellationToken cancellationToken);

    /// <summary>The providers the user holds a Valid key for, in one query: what a page may offer that needs a key of the user's own (FR-KEY-08, NFR-SEC-03).</summary>
    Task<IReadOnlySet<AiProvider>> ValidProvidersAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Validates with Binance (an authenticated request per allowed market plus the permissions read) and refuses withdrawal-enabled keys (FR-KEY-01, FR-KEY-02).</summary>
    Task<KeySaveResult> AddExchangeKeyAsync(Guid userId, ExchangeKeyInput input, CancellationToken cancellationToken);

    /// <summary>New credentials under the same id (FR-KEY-06): label, environment and markets stay; agents keep their reference.</summary>
    Task<KeySaveResult> ReplaceExchangeKeyAsync(Guid userId, Guid keyId, string? apiKey, string? apiSecret, CancellationToken cancellationToken);

    /// <summary>Re-validates a stored key now; an invalid key puts the agents using it into Error (FR-KEY-07).</summary>
    Task<KeyValidationResult> ValidateExchangeKeyAsync(Guid userId, Guid keyId, CancellationToken cancellationToken);

    /// <summary>Refused with <see cref="KeyDeletionStatus.AgentsInUse"/> unless <paramref name="stopAgents"/>, which stops the Running or Paused live agents on the key first (FR-KEY-06).</summary>
    Task<KeyDeletionResult> DeleteExchangeKeyAsync(Guid userId, Guid keyId, bool stopAgents, CancellationToken cancellationToken);

    /// <summary>Adds or replaces the user's key for a provider after listing its models; a success also syncs the catalog (FR-KEY-03, FR-SYNC-02).</summary>
    Task<KeySaveResult> SetProviderKeyAsync(Guid userId, AiProvider provider, string? apiKey, CancellationToken cancellationToken);

    /// <summary>Re-validates the stored key now and syncs the catalog when it is valid (FR-KEY-07, FR-SYNC-02).</summary>
    Task<KeyValidationResult> ValidateProviderKeyAsync(Guid userId, AiProvider provider, CancellationToken cancellationToken);

    /// <summary>Refused with <see cref="KeyDeletionStatus.AgentsInUse"/> unless <paramref name="stopAgents"/>, which stops the Running or Paused agents with an enabled council role on the provider (FR-KEY-06).</summary>
    Task<KeyDeletionResult> DeleteProviderKeyAsync(Guid userId, AiProvider provider, bool stopAgents, CancellationToken cancellationToken);
}
