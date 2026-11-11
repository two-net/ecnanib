using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Ecnanib.Accounts;

/// <summary>
/// A secret that is handed out once, in a link or in a cookie: the token of a verification link,
/// of a password reset link, or of a session. Whoever holds it is taken for the person it was
/// given to. The database keeps only its SHA-256 hash, so that reading the database gives nobody
/// a link or a session that works.
/// </summary>
internal static class SecretToken
{
    // 256 random bits: nobody finds one by trying, so a plain hash is enough to store it.
    private const int Length = 32;

    /// <summary>A new token, as the text to hand out and as the hash for the database.</summary>
    public static (string Text, byte[] Hash) Create()
    {
        var token = RandomNumberGenerator.GetBytes(Length);

        return (Base64Url.EncodeToString(token), SHA256.HashData(token));
    }

    /// <summary>The hash of a token that was handed back, or null when the text is not a token.</summary>
    public static byte[]? HashOf(string text)
    {
        Span<byte> token = stackalloc byte[Length];

        // A text of another length cannot be a token, and is not worth decoding. One of the
        // right length need not be a token either: it comes from a link or a cookie, as
        // whoever sent the request wrote it.
        if (text.Length != Base64Url.GetEncodedLength(Length)
            || Base64Url.DecodeFromChars(text, token, out _, out var decoded) != OperationStatus.Done
            || decoded != Length)
        {
            return null;
        }

        return SHA256.HashData(token);
    }
}
