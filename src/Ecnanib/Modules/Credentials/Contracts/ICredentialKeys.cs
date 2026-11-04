using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.Credentials.Contracts;

/// <summary>
/// A user's key, decrypted for one use (FR-KEY-09). Only Catalog, AiCouncil and Execution may use this,
/// which BannedApis enforces, and only Credentials may build an <see cref="AiKey"/> or
/// <see cref="ExchangeKey"/>, so every key the adapters see is a user's own (CON-11) and passed these
/// checks. The caller owns the secrets, and disposes them once the call returns. Never cache a key.
/// Each use stamps the credential's last use (FR-KEY-03), at most once a minute. See Credentials in
/// docs/architecture.md.
/// </summary>
public interface ICredentialKeys
{
    /// <exception cref="CredentialUnusableException">It isn't this user's AI credential, or it isn't valid.</exception>
    Task<AiKey> OpenAiKeyAsync(Guid userId, Guid credentialId, CancellationToken cancellationToken);

    /// <summary>
    /// The same key as <see cref="OpenAiKeyAsync"/>, for listing its models only (FR-CAT-02). Listing isn't a
    /// use, as validating isn't, so it leaves the last use alone. Otherwise every key would look used each sync.
    /// </summary>
    /// <exception cref="CredentialUnusableException">It isn't this user's AI credential, or it isn't valid.</exception>
    Task<AiKey> OpenAiKeyToListModelsAsync(Guid userId, Guid credentialId, CancellationToken cancellationToken);

    /// <summary>The user's AI credentials, whatever their status, oldest first.</summary>
    Task<IReadOnlyList<AiCredential>> ListAiAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Every user's AI credentials, whatever their status. The catalog syncs from all of them and merges the
    /// results (FR-CAT-02). It's a method of its own, rather than a null user, so that no slip widens a
    /// user's query to everyone (NFR-SEC-04).
    /// </summary>
    Task<IReadOnlyList<AiCredential>> ListAllAiAsync(CancellationToken cancellationToken);

    /// <summary>A Binance key for one market, which it must be enabled for (FR-KEY-05).</summary>
    /// <exception cref="CredentialUnusableException">
    /// It isn't this user's Binance credential, it isn't valid, or it isn't enabled for <paramref name="market"/>.
    /// </exception>
    Task<ExchangeKey> OpenExchangeKeyAsync(Guid userId, Guid credentialId, Market market, CancellationToken cancellationToken);
}

/// <summary>An AI credential, as IDs and a status. It holds no secret and no label.</summary>
/// <param name="Provider">The AI provider's name, such as <c>OpenAI</c>.</param>
public sealed record AiCredential(Guid Id, Guid UserId, string Provider, CredentialStatus Status);

public enum CredentialUnusableReason
{
    /// <summary>No such credential of that kind for this user.</summary>
    NotFound,

    /// <summary>Its status isn't <see cref="CredentialStatus.Valid"/>.</summary>
    NotValid,

    /// <summary>Binance said the key can't trade on that market.</summary>
    MarketNotEnabled,
}

/// <summary>A key that can't be used. The message holds the credential's ID and the reason, and nothing else.</summary>
public sealed class CredentialUnusableException(Guid credentialId, CredentialUnusableReason reason)
    : Exception($"Credential {credentialId} can't be used: {reason}.")
{
    public Guid CredentialId { get; } = credentialId;

    public CredentialUnusableReason Reason { get; } = reason;
}
