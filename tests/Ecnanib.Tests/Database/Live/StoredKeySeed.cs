using System.Text;
using Ecnanib.Database;
using Ecnanib.Database.Entities;

namespace Ecnanib.Tests.Database.Live;

/// <summary>
/// Encrypts seeded key rows exactly as the application does, under the hosts' test key (FR-KEY-04), so every reader
/// (the resolver, the re-validation, the model sync, the pages) opens what a test wrote through the context.
/// </summary>
internal static class StoredKeySeed
{
    /// <summary>The production protector under <see cref="DatabaseTestHost.EncryptionKey"/>, without a previous key; as the class, for its check-value members.</summary>
    internal static readonly StoredKeyProtector Keyed = new(Convert.FromBase64String(DatabaseTestHost.EncryptionKey), null);

    /// <summary>The same protector as the contract the application sees, so the entity extensions apply.</summary>
    public static readonly IStoredKeyProtector Protector = Keyed;

    /// <summary>Protects the pair under the row's own id, assigning one first when the entity has none yet (the ciphertext is bound to it).</summary>
    public static ExchangeKey Protect(ExchangeKey key, string apiKey, string apiSecret)
    {
        if (key.Id == Guid.Empty)
            key.Id = Guid.CreateVersion7();
        Protector.Protect(key, apiKey, apiSecret);
        return key;
    }

    public static ProviderKey Protect(ProviderKey key, string apiKey)
    {
        if (key.Id == Guid.Empty)
            key.Id = Guid.CreateVersion7();
        Protector.Protect(key, apiKey);
        return key;
    }

    /// <summary>Asserts the text does not sit in the stored bytes (a byte-for-byte view, so an ASCII value is found wherever it starts).</summary>
    public static void AssertHidden(byte[] stored, string value) => Assert.DoesNotContain(value, Encoding.Latin1.GetString(stored), StringComparison.Ordinal);

    /// <summary>A value as the ProtectStoredKeys migration leaves it: the UTF-8 bytes of the text an earlier version stored.</summary>
    public static byte[] Plain(string value) => Encoding.UTF8.GetBytes(value);
}
