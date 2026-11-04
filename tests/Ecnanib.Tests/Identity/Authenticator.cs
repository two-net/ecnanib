using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// The user's authenticator app, written separately from the application's, the way PasswordHasherTests
/// checks hashes made by another implementation. It works on strings of bits rather than shifts, so the
/// two don't share a mistake. AuthenticatorTests pins it to the RFCs' own vectors, and the two-factor
/// tests then show the application agrees with it.
/// </summary>
internal static class Authenticator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The 6-digit code an app shows at <paramref name="time"/> for a base32 key, spaces allowed.</summary>
    public static string Code(string base32Key, DateTimeOffset time) =>
        Code(Base32Decode(base32Key), time.ToUnixTimeSeconds() / 30, digits: 6);

    /// <summary>RFC 4226 HOTP with SHA-1, which RFC 6238 runs with the time step as its counter.</summary>
    public static string Code(byte[] key, long counter, int digits)
    {
        var message = BitConverter.GetBytes(counter);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(message);
        }

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0xF;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);

        return (value % modulus).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    public static string Base32Encode(byte[] data)
    {
        var bits = string.Concat(data.Select(value => Convert.ToString(value, 2).PadLeft(8, '0')));
        var padded = bits.PadRight((bits.Length + 4) / 5 * 5, '0');

        return string.Concat(Enumerable.Range(0, padded.Length / 5).Select(index => Alphabet[Convert.ToInt32(padded.Substring(index * 5, 5), 2)]));
    }

    public static byte[] Base32Decode(string text)
    {
        var bits = new StringBuilder();

        foreach (var character in text.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant())
        {
            bits.Append(Convert.ToString(Alphabet.IndexOf(character, StringComparison.Ordinal), 2).PadLeft(5, '0'));
        }

        return [.. Enumerable.Range(0, bits.Length / 8).Select(index => Convert.ToByte(bits.ToString(index * 8, 8), 2))];
    }
}

/// <summary>
/// The clock the application reads time steps from. It runs with real time, and a test moves it on a
/// step so that each code it needs is new: a code works once.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    /// <summary>The code an app would show for <paramref name="key"/> in the next step, which moves the clock there.</summary>
    public string NextCode(string key)
    {
        Advance(TimeSpan.FromSeconds(30));

        return Authenticator.Code(key, GetUtcNow());
    }
}
