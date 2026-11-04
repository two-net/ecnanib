using System.Security.Cryptography;
using System.Text;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using NSec.Cryptography;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// Signs the exact query string that's sent (EXT-BIN-03, FR-KEY-06). Its values are already
/// percent-encoded, which Spot requires before signing since 2026-01-15. The secret is revealed here and
/// nowhere else, as bytes, and nothing derived from it outlives the call.
/// </summary>
internal static class RequestSigner
{
    /// <returns>The signature as it goes in the query: hex for HMAC, percent-encoded base64 for Ed25519.</returns>
    /// <exception cref="ArgumentException">An Ed25519 secret that isn't a PKCS#8 PEM private key.</exception>
    public static string Sign(ExchangeKey key, string payload)
    {
        var data = Encoding.ASCII.GetBytes(payload);

        return key.Type switch
        {
            ExchangeKeyType.Hmac => SignHmac(key.Secret.RevealUtf8(), data),
            ExchangeKeyType.Ed25519 => Uri.EscapeDataString(Convert.ToBase64String(SignEd25519(key.Secret.RevealUtf8(), data))),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }

    private static string SignHmac(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> data)
    {
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, data, mac);

        return Convert.ToHexStringLower(mac);
    }

    private static byte[] SignEd25519(ReadOnlySpan<byte> pem, ReadOnlySpan<byte> data)
    {
        Key privateKey;

        try
        {
            privateKey = Key.Import(SignatureAlgorithm.Ed25519, pem, KeyBlobFormat.PkixPrivateKeyText);
        }
        catch (FormatException)
        {
            // Without the inner exception, which describes the key's content.
            throw new ArgumentException("The Ed25519 secret isn't a PKCS#8 PEM private key.", nameof(pem));
        }

        using (privateKey)
        {
            return SignatureAlgorithm.Ed25519.Sign(privateKey, data);
        }
    }
}
