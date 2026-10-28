using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// One AI provider key per user and provider (FR-KEY-03, FR-KEY-04), encrypted at rest by the database module's
/// <c>IStoredKeyProtector</c> under <c>Database:EncryptionKey</c> (A-02).
/// </summary>
public sealed class ProviderKey
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public AiProvider Provider { get; set; }

    /// <summary>The API key as nonce, tag and ciphertext bound to this row (<c>api_key</c>); read it through <c>IStoredKeyProtector</c> only.</summary>
    public required byte[] ProtectedApiKey { get; set; }

    /// <summary>What the column holds: <see cref="StoredKeyCipher.Plain"/> only on a row an earlier version wrote, until the first start encrypts it.</summary>
    public StoredKeyCipher Cipher { get; set; } = StoredKeyCipher.Plain;

    public KeyStatus Status { get; set; } = KeyStatus.Unvalidated;

    public DateTimeOffset? LastValidatedAt { get; set; }

    public string? LastValidationError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
