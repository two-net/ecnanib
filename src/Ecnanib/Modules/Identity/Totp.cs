using System.Buffers.Binary;
using System.Security.Cryptography;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// RFC 6238 time-based one-time passwords, with the parameters every authenticator app supports: HMAC-SHA1,
/// 6 digits and a 30-second step. They're constants, because an app that ignores a different value
/// would show codes that never work. Keys are 20 random bytes, as RFC 4226 recommends, held as base32.
/// </summary>
internal static class Totp
{
    public const int Digits = 6;

    public const int StepSeconds = 30;

    public const int KeyLength = 20;

    /// <summary>Steps either side of now that are accepted, for a phone's clock that's a little off.</summary>
    private const int Drift = 1;

    private const int Modulus = 1_000_000;

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>A new random key as base32 text, which is what the secret store holds and the user types.</summary>
    public static SecretValue NewKey()
    {
        var key = GC.AllocateUninitializedArray<byte>(KeyLength, pinned: true);
        var text = GC.AllocateUninitializedArray<byte>(Base32.EncodedLength(KeyLength), pinned: true);

        try
        {
            RandomNumberGenerator.Fill(key);
            Base32.Encode(key, text);

            return SecretValue.FromUtf8(text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(text);
        }
    }

    /// <summary>Whether a normalized code has the shape of a TOTP code, so it's worth checking.</summary>
    public static bool IsCode(string normalized) => normalized.Length == Digits && normalized.All(char.IsAsciiDigit);

    /// <summary>
    /// The step that <paramref name="code"/> belongs to, within the drift of <paramref name="now"/> and
    /// later than <paramref name="lastUsedStep"/>, or null. Every step in the window is computed and
    /// compared in fixed time, so the time taken doesn't say which matched. If two match, the later wins,
    /// which leaves fewer codes usable afterwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">The stored key isn't base32.</exception>
    public static long? Match(SecretValue key, string code, long now, long lastUsedStep)
    {
        if (!IsCode(code))
        {
            return null;
        }

        var text = key.RevealUtf8();
        var bytes = GC.AllocateUninitializedArray<byte>(Base32.DecodedLength(text.Length), pinned: true);

        try
        {
            if (!Base32.TryDecode(text, bytes, out var written))
            {
                throw new InvalidOperationException("A stored TOTP key isn't base32.");
            }

            Span<byte> given = stackalloc byte[Digits];
            Span<byte> expected = stackalloc byte[Digits];

            for (var index = 0; index < Digits; index++)
            {
                given[index] = (byte)code[index];
            }

            long? matched = null;

            for (var step = now - Drift; step <= now + Drift; step++)
            {
                Format(Code(bytes.AsSpan(0, written), step), expected);

                if (CryptographicOperations.FixedTimeEquals(expected, given) && step > lastUsedStep)
                {
                    matched = step;
                }
            }

            return matched;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>The code for one step: HOTP (RFC 4226) with the step as its counter.</summary>
    private static int Code(ReadOnlySpan<byte> key, long step)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];

        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var truncated = BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF;

        return truncated % Modulus;
    }

    private static void Format(int code, Span<byte> destination)
    {
        for (var index = Digits - 1; index >= 0; index--)
        {
            destination[index] = (byte)('0' + (code % 10));
            code /= 10;
        }
    }
}
