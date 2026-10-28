using Ecnanib.Database.Entities;

namespace Ecnanib.Database;

/// <summary>The stored cell a value belongs to; with the row id it is the additional authenticated data, so a ciphertext opens only in its own cell.</summary>
public enum StoredKeyColumn
{
    ExchangeApiKey = 1,
    ExchangeApiSecret = 2,
    ProviderApiKey = 3,
}

/// <summary>
/// The users' exchange and provider keys at rest (FR-KEY-04, A-02): AES-256-GCM under <c>Database:EncryptionKey</c>,
/// a fresh 12-byte nonce per write, a 16-byte tag, and the table, column and row id as additional authenticated data,
/// stored as nonce || tag || ciphertext in the row's <c>bytea</c> column with <see cref="StoredKeyCipher.AesGcm"/> in
/// its <c>cipher</c> column. The keys module writes and reads through this; so does the model-sync job, which reads
/// provider keys itself. A value that does not open is infrastructure failure, never a user mistake.
/// </summary>
public interface IStoredKeyProtector
{
    byte[] Protect(StoredKeyColumn column, Guid rowId, string value);

    /// <summary>
    /// Throws <see cref="StoredKeyProtectionException"/> when the value does not open: the wrong key, another cell's
    /// ciphertext, a tampered or truncated value, or a row still <see cref="StoredKeyCipher.Plain"/>, which only the
    /// start-up step may read.
    /// </summary>
    string Unprotect(StoredKeyColumn column, Guid rowId, StoredKeyCipher cipher, byte[] stored);
}

public static class StoredKeyProtectorExtensions
{
    /// <summary>Stores both values of the pair under the key's own id and marks the row encrypted.</summary>
    public static void Protect(this IStoredKeyProtector protector, ExchangeKey key, string apiKey, string apiSecret)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(key);
        key.ProtectedApiKey = protector.Protect(StoredKeyColumn.ExchangeApiKey, key.Id, apiKey);
        key.ProtectedApiSecret = protector.Protect(StoredKeyColumn.ExchangeApiSecret, key.Id, apiSecret);
        key.Cipher = StoredKeyCipher.AesGcm;
    }

    public static void Protect(this IStoredKeyProtector protector, ProviderKey key, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(key);
        key.ProtectedApiKey = protector.Protect(StoredKeyColumn.ProviderApiKey, key.Id, apiKey);
        key.Cipher = StoredKeyCipher.AesGcm;
    }

    public static (string ApiKey, string ApiSecret) Unprotect(this IStoredKeyProtector protector, ExchangeKey key)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(key);
        return (
            protector.Unprotect(StoredKeyColumn.ExchangeApiKey, key.Id, key.Cipher, key.ProtectedApiKey),
            protector.Unprotect(StoredKeyColumn.ExchangeApiSecret, key.Id, key.Cipher, key.ProtectedApiSecret));
    }

    public static string Unprotect(this IStoredKeyProtector protector, ProviderKey key)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(key);
        return protector.Unprotect(StoredKeyColumn.ProviderApiKey, key.Id, key.Cipher, key.ProtectedApiKey);
    }
}
