using System.Text.Json;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// A user's Binance API key pair (FR-KEY-01, FR-KEY-04). The key and the secret are encrypted at rest by the database
/// module's <c>IStoredKeyProtector</c> under <c>Database:EncryptionKey</c> (A-02); the UI shows at most the last four
/// characters and the value never reaches logs, prompts or error messages (FR-KEY-04, FR-LOG-05).
/// </summary>
public sealed class ExchangeKey
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public required string Label { get; set; }

    /// <summary>Fixed per key (EX-04).</summary>
    public ExchangeEnvironment Environment { get; set; }

    public bool AllowSpot { get; set; }

    public bool AllowFutures { get; set; }

    /// <summary>The API key as nonce, tag and ciphertext bound to this row (<c>api_key</c>); read it through <c>IStoredKeyProtector</c> only.</summary>
    public required byte[] ProtectedApiKey { get; set; }

    /// <summary>The API secret, held like <see cref="ProtectedApiKey"/> (<c>api_secret</c>).</summary>
    public required byte[] ProtectedApiSecret { get; set; }

    /// <summary>What the two columns hold: <see cref="StoredKeyCipher.Plain"/> only on a row an earlier version wrote, until the first start encrypts it.</summary>
    public StoredKeyCipher Cipher { get; set; } = StoredKeyCipher.Plain;

    /// <summary>The <c>ApiKeyPermissions</c> read on validation (FR-KEY-02); withdrawal-enabled keys are never stored.</summary>
    public JsonElement? PermissionsSnapshot { get; set; }

    public KeyStatus Status { get; set; } = KeyStatus.Unvalidated;

    public DateTimeOffset? LastValidatedAt { get; set; }

    /// <summary>Why the last validation failed, shown as the cause in the UI (FR-KEY-07); never contains the key.</summary>
    public string? LastValidationError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
