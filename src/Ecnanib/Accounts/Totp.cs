using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Ecnanib.Accounts;

/// <summary>
/// The codes of an authenticator app: time-based one-time passwords as RFC 6238 defines them, with
/// HMAC-SHA1, six digits, and a new code every 30 seconds (FR-ACC-3). These are the values every
/// authenticator app uses unless it is told otherwise. See docs/accounts.md.
/// </summary>
/// <remarks>
/// The app and the application share a key, and each works out the code of the present time from
/// it. .NET has what the algorithm needs, so no package is used.
/// </remarks>
public static class Totp
{
    /// <summary>How many digits a code has.</summary>
    public const int Digits = 6;

    /// <summary>How long one code is the code of the present time. Time is counted in steps of this length.</summary>
    public const int StepSeconds = 30;

    /// <summary>The length of a key in bytes: 160 bits, as RFC 4226 recommends.</summary>
    public const int KeyLength = 20;

    // The alphabet of Base32 in RFC 4648, which is what an app takes a key in.
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new random key.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyLength);

    /// <summary>The step that a moment falls in: the count of whole steps since the start of 1970.</summary>
    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code of a key in a step, with its leading zeros.</summary>
    public static string CodeAt(ReadOnlySpan<byte> key, long step)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        // Four bytes of the hash, at the place its last four bits name, without the sign bit.
        var offset = hash[^1] & 0x0F;
        var number = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, sizeof(int))) & 0x7FFFFFFF;

        return (number % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The code of a key at a moment.</summary>
    public static string CodeAt(ReadOnlySpan<byte> key, DateTimeOffset time) => CodeAt(key, StepAt(time));

    /// <summary>A key as the text that a user can type into an app: Base32, without padding.</summary>
    public static string TextOf(ReadOnlySpan<byte> key)
    {
        var text = new char[((key.Length * 8) + 4) / 5];
        var bits = 0;
        var held = 0;
        var position = 0;

        foreach (var value in key)
        {
            held = (held << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                text[position++] = Base32Alphabet[(held >> bits) & 0x1F];
            }
        }

        if (bits > 0)
        {
            text[position] = Base32Alphabet[(held << (5 - bits)) & 0x1F];
        }

        return new string(text);
    }

    /// <summary>
    /// The address that a QR code carries to an app: the key, with the name of the service and of
    /// the account, in the form that authenticator apps read.
    /// </summary>
    /// <param name="issuer">The name of the service, which the app shows beside the code.</param>
    /// <param name="account">The name of the account, which is its email address.</param>
    /// <param name="key">The key.</param>
    public static string UriFor(string issuer, string account, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(account);

        // The colon between the two is the separator of the form, so one inside a name is escaped.
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";

        return $"otpauth://totp/{label}?secret={TextOf(key)}&issuer={Uri.EscapeDataString(issuer)}";
    }
}
