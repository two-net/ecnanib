using System.Security.Cryptography;
using System.Text;
using Ecnanib.Database.Configuration;
using Ecnanib.Database.Entities;
using Microsoft.Extensions.Options;

namespace Ecnanib.Database;

/// <summary>
/// <see cref="IStoredKeyProtector"/> over the BCL <see cref="AesGcm"/> with the current key and, for a rotation, the
/// previous one. One cipher instance per call: the operations are rare (a save, a validation, an evaluation's key
/// resolution) and the instance is then never shared between threads. Nothing here logs; the exceptions name no value
/// and no key.
/// </summary>
internal sealed class StoredKeyProtector : IStoredKeyProtector
{
    internal const int KeySize = 32;
    internal const int NonceSize = 12;
    internal const int TagSize = 16;

    /// <summary>What a stored value adds to its plaintext's length.</summary>
    internal const int Overhead = NonceSize + TagSize;

    private readonly byte[] _current;
    private readonly byte[]? _previous;

    public StoredKeyProtector(IOptions<DatabaseOptions> options)
        : this(ParseKey(options.Value.EncryptionKey, nameof(DatabaseOptions.EncryptionKey)),
            string.IsNullOrWhiteSpace(options.Value.PreviousEncryptionKey) ? null : ParseKey(options.Value.PreviousEncryptionKey, nameof(DatabaseOptions.PreviousEncryptionKey)))
    {
    }

    /// <summary>The tests' and the start-up step's constructor, over raw keys.</summary>
    internal StoredKeyProtector(byte[] current, byte[]? previous)
    {
        if (!AesGcm.IsSupported)
            throw new PlatformNotSupportedException("AES-GCM is not available on this platform, so the stored keys cannot be protected.");
        if (current.Length != KeySize || previous is { Length: not KeySize })
            throw new ArgumentException($"A key is {KeySize} bytes.");
        _current = current;
        _previous = previous;
    }

    internal bool HasPreviousKey => _previous is not null;

    /// <summary>Whether the text is the base64 of exactly <see cref="KeySize"/> bytes; the validator's rule.</summary>
    internal static bool TryParseKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;
        Span<byte> buffer = stackalloc byte[3 * KeySize];
        if (!Convert.TryFromBase64String(value.Trim(), buffer, out var written) || written != KeySize)
            return false;
        key = buffer[..written].ToArray();
        return true;
    }

    public byte[] Protect(StoredKeyColumn column, Guid rowId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Seal(_current, Aad(column, rowId), Encoding.UTF8.GetBytes(value));
    }

    public string Unprotect(StoredKeyColumn column, Guid rowId, StoredKeyCipher cipher, byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (cipher != StoredKeyCipher.AesGcm)
            throw new StoredKeyProtectionException($"The stored key is held as {cipher}, not encrypted.");
        return Open(_current, Aad(column, rowId), stored);
    }

    /// <summary>The rotation path: opens a value sealed under <c>Database:PreviousEncryptionKey</c>.</summary>
    internal string UnprotectWithPrevious(StoredKeyColumn column, Guid rowId, byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return Open(_previous ?? throw new InvalidOperationException("No previous key is configured."), Aad(column, rowId), stored);
    }

    /// <summary>The check value the start-up step keeps in <c>system_keys</c>: the purpose sealed under the current key.</summary>
    internal byte[] ProtectCheckValue(string purpose) => Seal(_current, CheckAad(purpose), Encoding.UTF8.GetBytes(purpose));

    /// <summary>Whether the stored check value opens under the current or the previous key and holds the purpose.</summary>
    internal bool CheckValueOpens(string purpose, byte[] stored, bool withPrevious)
    {
        var key = withPrevious ? _previous : _current;
        if (key is null)
            return false;
        try
        {
            return Open(key, CheckAad(purpose), stored) == purpose;
        }
        catch (StoredKeyProtectionException)
        {
            return false;
        }
    }

    private static byte[] ParseKey(string? value, string setting) =>
        TryParseKey(value, out var key) ? key : throw new ArgumentException($"{DatabaseOptions.SectionName}:{setting} must be the base64 encoding of exactly {KeySize} bytes.");

    private static byte[] Aad(StoredKeyColumn column, Guid rowId)
    {
        var cell = column switch
        {
            StoredKeyColumn.ExchangeApiKey => "exchange_keys.api_key:",
            StoredKeyColumn.ExchangeApiSecret => "exchange_keys.api_secret:",
            StoredKeyColumn.ProviderApiKey => "provider_keys.api_key:",
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown stored key column."),
        };
        return Encoding.UTF8.GetBytes(cell + rowId.ToString("D"));
    }

    private static byte[] CheckAad(string purpose) => Encoding.UTF8.GetBytes("system_keys.value:" + purpose);

    private static byte[] Seal(byte[] key, byte[] aad, ReadOnlySpan<byte> plaintext)
    {
        var stored = new byte[Overhead + plaintext.Length];
        var nonce = stored.AsSpan(0, NonceSize);
        var tag = stored.AsSpan(NonceSize, TagSize);
        var ciphertext = stored.AsSpan(Overhead);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return stored;
    }

    private static string Open(byte[] key, byte[] aad, byte[] stored)
    {
        if (stored.Length < Overhead)
            throw new StoredKeyProtectionException("The stored key is shorter than a nonce and a tag.");
        var plaintext = new byte[stored.Length - Overhead];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(stored.AsSpan(0, NonceSize), stored.AsSpan(Overhead), stored.AsSpan(NonceSize, TagSize), plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            // AuthenticationTagMismatchException: the wrong key, another cell's value, or a changed byte.
            throw new StoredKeyProtectionException("The stored key does not open under the configured key.", ex);
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
