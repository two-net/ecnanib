using System.Security.Cryptography;

namespace Ecnanib.Platform.Secrets;

/// <summary>
/// AES-256-GCM (FR-KEY-07). A sealed secret is <c>[version 1][12-byte nonce][16-byte tag][ciphertext]</c>,
/// and its key ID is stored beside it. Nonces are random, which is safe far beyond the number of
/// secrets one key will ever seal (2^32). See Secrets in docs/architecture.md.
/// </summary>
internal static class SecretCipher
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    public static byte[] Seal(MasterKey key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var sealedBytes = new byte[HeaderSize + plaintext.Length];
        var nonce = sealedBytes.AsSpan(1, NonceSize);

        sealedBytes[0] = Version;
        RandomNumberGenerator.Fill(nonce);

        // A new instance each time, because one isn't safe to share between threads.
        using var aes = new AesGcm(key.Bytes, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(HeaderSize), sealedBytes.AsSpan(1 + NonceSize, TagSize), associatedData);

        return sealedBytes;
    }

    /// <returns>The plaintext, in a pinned buffer that the caller clears with <see cref="CryptographicOperations.ZeroMemory"/>.</returns>
    /// <exception cref="CryptographicException">The bytes aren't a sealed secret, or were sealed with another key or associated data.</exception>
    public static byte[] Open(MasterKey key, ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> associatedData)
    {
        if (sealedBytes.Length < HeaderSize || sealedBytes[0] != Version)
        {
            throw new CryptographicException("The stored bytes aren't a sealed secret this version can read.");
        }

        var plaintext = GC.AllocateUninitializedArray<byte>(sealedBytes.Length - HeaderSize, pinned: true);

        try
        {
            using var aes = new AesGcm(key.Bytes, TagSize);
            aes.Decrypt(
                sealedBytes.Slice(1, NonceSize),
                sealedBytes[HeaderSize..],
                sealedBytes.Slice(1 + NonceSize, TagSize),
                plaintext,
                associatedData);

            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }
}
