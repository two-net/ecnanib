using System.Buffers.Text;
using System.Security.Cryptography;

namespace Ecnanib.Accounts;

/// <summary>The rules of verifying an email address (FR-ACC-1), and the link that does it.</summary>
public static class EmailVerification
{
    /// <summary>How long a verification link works after it was sent.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    /// <summary>The path of the page that a verification link opens.</summary>
    public const string Path = "/verify-email";

    /// <summary>
    /// The query parameter that carries the token. It is called this so that the one place that
    /// masks secrets in logs recognises it in a logged address. See docs/logging.md.
    /// </summary>
    public const string TokenParameter = "token";

    /// <summary>The link for a token, under the address users reach the service at.</summary>
    public static Uri LinkFor(Uri publicUrl, string token)
    {
        ArgumentNullException.ThrowIfNull(publicUrl);
        ArgumentNullException.ThrowIfNull(token);

        return new Uri(publicUrl, $"{Path}?{TokenParameter}={Uri.EscapeDataString(token)}");
    }
}

/// <summary>
/// The secret in a verification link. The link carries the token, and the database keeps only
/// its SHA-256 hash, so that reading the database gives nobody a link that works.
/// </summary>
internal static class VerificationToken
{
    // 256 random bits: nobody finds one by trying, so a plain hash is enough to store it.
    private const int Length = 32;

    /// <summary>A new token, as the text for the link and as the hash for the database.</summary>
    public static (string Text, byte[] Hash) Create()
    {
        var token = RandomNumberGenerator.GetBytes(Length);

        return (Base64Url.EncodeToString(token), SHA256.HashData(token));
    }

    /// <summary>The hash of the token in a link, or null when the text is not a token.</summary>
    public static byte[]? HashOf(string text)
    {
        Span<byte> token = stackalloc byte[Length];

        // A text of another length cannot be a token, and is not worth decoding.
        if (text.Length != Base64Url.GetEncodedLength(Length)
            || !Base64Url.TryDecodeFromChars(text, token, out var decoded)
            || decoded != Length)
        {
            return null;
        }

        return SHA256.HashData(token);
    }
}
