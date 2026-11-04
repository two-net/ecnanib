using System.Text;
using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// What's checked before anything is sent: a key's shape and a label's text. The checks read a secret only
/// as bytes, and never say what was wrong with it beyond its shape.
/// </summary>
internal static class KeyInput
{
    private const string PemBegin = "-----BEGIN PRIVATE KEY-----";
    private const string PemEnd = "-----END PRIVATE KEY-----";

    /// <summary>
    /// An API key or HMAC secret: printable ASCII with no spaces, as every provider's is, from
    /// <see cref="CredentialLimits.KeyMinLength"/> to <see cref="CredentialLimits.KeyMaxLength"/> characters.
    /// </summary>
    public static bool IsKey(SecretValue value)
    {
        var bytes = value.RevealUtf8();

        if (bytes.Length is < CredentialLimits.KeyMinLength or > CredentialLimits.KeyMaxLength)
        {
            return false;
        }

        foreach (var character in bytes)
        {
            if (character is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// An Ed25519 private key as Binance's generator writes it: one PKCS#8 PEM block. The adapter imports it
    /// properly when it signs, so this only keeps out what plainly isn't one.
    /// </summary>
    public static bool IsPrivateKey(SecretValue value)
    {
        var bytes = value.RevealUtf8();

        if (bytes.Length > CredentialLimits.PrivateKeyMaxLength)
        {
            return false;
        }

        foreach (var character in bytes)
        {
            if (character is not ((>= 0x20 and <= 0x7E) or (byte)'\n' or (byte)'\r'))
            {
                return false;
            }
        }

        var begin = bytes.IndexOf(Encoding.ASCII.GetBytes(PemBegin));
        var end = bytes.IndexOf(Encoding.ASCII.GetBytes(PemEnd));

        return begin >= 0 && end > begin;
    }

    public static bool IsSecret(BinanceKeyType type, SecretValue value) =>
        type == BinanceKeyType.Ed25519 ? IsPrivateKey(value) : IsKey(value);

    /// <summary>The last four characters FR-KEY-03 shows. <see cref="IsKey"/> has checked it's long ASCII.</summary>
    public static string LastFour(SecretValue apiKey) => Encoding.ASCII.GetString(apiKey.RevealUtf8()[^4..]);

    /// <summary>
    /// What PostgreSQL can store and a page can show: no control characters, which include a NUL, and no
    /// half of a surrogate pair, trimmed. Null when nothing is left or it's longer than
    /// <see cref="CredentialLimits.LabelMaxLength"/>.
    /// </summary>
    public static string? Label(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var kept = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    kept.Append(character).Append(text[++index]);
                }
            }
            else if (!char.IsLowSurrogate(character) && !char.IsControl(character))
            {
                kept.Append(character);
            }
        }

        var label = kept.ToString().Trim();

        return label.Length is 0 or > CredentialLimits.LabelMaxLength ? null : label;
    }
}
