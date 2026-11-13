using System.Security.Cryptography;
using System.Text;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Platform.Security;

/// <summary>
/// A secret as it is stored: encrypted, with the id of the master key that encrypted it (FR-KEY-6).
/// </summary>
/// <param name="KeyId">The id of the master key, as the configuration file lists it.</param>
/// <param name="Value">The nonce, the ciphertext, and the tag, in that order.</param>
public sealed record EncryptedSecret(string KeyId, byte[] Value);

/// <summary>
/// Encrypts the secrets that the database holds, and decrypts them for the call that needs them
/// (FR-KEY-6, FR-KEY-9). The keys are the master keys of the configuration file: new values are
/// encrypted with the active one, and a value is read with the one it names, so that the active
/// key can change while earlier values still read. An earlier value is then encrypted again with
/// the active key, when the application starts: see <see cref="StoredSecretReencryption"/>
/// (FR-KEY-7). See docs/security.md.
/// </summary>
/// <remarks>
/// The encryption is AES-256-GCM, which is authenticated: a value that was changed does not
/// decrypt. Every value has a nonce of its own.
/// </remarks>
public sealed class StoredSecrets
{
    // What AES-GCM is specified with: a nonce of 96 bits and a tag of 128.
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly string _activeKeyId;
    private readonly Dictionary<string, byte[]> _keys;

    public StoredSecrets(SecuritySettings security)
    {
        ArgumentNullException.ThrowIfNull(security);

        // Decoded once. The ids ignore case, as they do in the configuration file.
        _keys = security.MasterKeys.ToDictionary(
            key => key.Key, key => Convert.FromBase64String(key.Value.Reveal()), StringComparer.OrdinalIgnoreCase);

        // The id as the file lists the key, however the file names the active one.
        _activeKeyId = _keys.Keys.Single(id => string.Equals(id, security.ActiveKeyId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The id of the master key that encrypts new values, as the configuration file lists it.</summary>
    public string ActiveKeyId => _activeKeyId;

    /// <summary>The ids of every master key the configuration file lists, as it lists them.</summary>
    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    /// <summary>Whether the configuration file lists a master key under an id. Ids ignore case.</summary>
    public bool Lists(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);

        return _keys.ContainsKey(keyId);
    }

    /// <summary>Encrypts a secret with the active master key.</summary>
    /// <param name="secret">The secret.</param>
    /// <param name="boundTo">
    /// What the value belongs to, such as a purpose and the id of a user. The value decrypts only
    /// when the same text is given again, so a value that is copied to another row is of no use there.
    /// </param>
    public EncryptedSecret Encrypt(ReadOnlySpan<byte> secret, string boundTo)
    {
        ArgumentNullException.ThrowIfNull(boundTo);

        var value = new byte[NonceLength + secret.Length + TagLength];
        var nonce = value.AsSpan(0, NonceLength);
        var ciphertext = value.AsSpan(NonceLength, secret.Length);
        var tag = value.AsSpan(NonceLength + secret.Length, TagLength);

        // Random, and new for every value: a nonce must never be used twice with one key.
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_keys[_activeKeyId], TagLength);
        aes.Encrypt(nonce, secret, ciphertext, tag, Encoding.UTF8.GetBytes(boundTo));

        return new EncryptedSecret(_activeKeyId, value);
    }

    /// <summary>Decrypts a stored secret with the master key it names.</summary>
    /// <param name="stored">The value, as <see cref="Encrypt"/> returned it.</param>
    /// <param name="boundTo">What the value belongs to, as it was given when the value was encrypted.</param>
    /// <returns>The secret. It is the caller's to use and to forget.</returns>
    /// <exception cref="CryptographicException">
    /// The value was changed, belongs to something else, or names a master key that the
    /// configuration file does not list.
    /// </exception>
    public byte[] Decrypt(EncryptedSecret stored, string boundTo)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(boundTo);

        if (!_keys.TryGetValue(stored.KeyId, out var key))
        {
            throw new CryptographicException("A stored secret names a master key that the configuration file does not list.");
        }

        if (stored.Value.Length < NonceLength + TagLength)
        {
            throw new CryptographicException("A stored secret is shorter than any encrypted value.");
        }

        var nonce = stored.Value.AsSpan(0, NonceLength);
        var ciphertext = stored.Value.AsSpan(NonceLength, stored.Value.Length - NonceLength - TagLength);
        var tag = stored.Value.AsSpan(stored.Value.Length - TagLength);
        var secret = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, secret, Encoding.UTF8.GetBytes(boundTo));

        return secret;
    }

    /// <summary>
    /// Encrypts a stored secret again, with the active master key and a new nonce (FR-KEY-7). It
    /// is read with the master key it names, and the secret is cleared before this returns: it
    /// is handed to nobody (FR-KEY-9).
    /// </summary>
    /// <param name="stored">The value, as <see cref="Encrypt"/> returned it.</param>
    /// <param name="boundTo">What the value belongs to, as it was given when the value was encrypted.</param>
    /// <returns>The same secret, bound to the same thing, as the active master key encrypts it.</returns>
    /// <exception cref="CryptographicException">The value cannot be read: see <see cref="Decrypt"/>.</exception>
    public EncryptedSecret Reencrypt(EncryptedSecret stored, string boundTo)
    {
        var secret = Decrypt(stored, boundTo);
        try
        {
            return Encrypt(secret, boundTo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
