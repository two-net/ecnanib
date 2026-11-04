namespace Ecnanib.Modules.Identity;

/// <summary>
/// RFC 4648 base32 without padding, which is how authenticator apps take a TOTP key. It works on ASCII
/// bytes rather than strings, so a key can stay in buffers that are cleared after use.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static int EncodedLength(int byteCount) => ((byteCount * 8) + 4) / 5;

    public static int DecodedLength(int charCount) => charCount * 5 / 8;

    /// <summary>Uppercase. <paramref name="destination"/> must be <see cref="EncodedLength"/> long.</summary>
    public static void Encode(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(destination.Length, EncodedLength(data.Length), nameof(destination));

        int buffer = 0, bits = 0, index = 0;

        foreach (var value in data)
        {
            // At most 12 bits are ever pending, so the mask keeps the buffer from overflowing.
            buffer = ((buffer << 8) | value) & 0xFFF;
            bits += 8;

            while (bits >= 5)
            {
                destination[index++] = (byte)Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            destination[index] = (byte)Alphabet[(buffer << (5 - bits)) & 31];
        }
    }

    /// <summary>
    /// Either case. False for anything outside the alphabet, including padding, or when
    /// <paramref name="destination"/> is too short. Leftover bits at the end are ignored.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> ascii, Span<byte> destination, out int written)
    {
        int buffer = 0, bits = 0;
        written = 0;

        foreach (var character in ascii)
        {
            var value = character switch
            {
                >= (byte)'A' and <= (byte)'Z' => character - 'A',
                >= (byte)'a' and <= (byte)'z' => character - 'a',
                >= (byte)'2' and <= (byte)'7' => character - '2' + 26,
                _ => -1,
            };

            if (value < 0)
            {
                return false;
            }

            buffer = ((buffer << 5) | value) & 0xFFF;
            bits += 5;

            if (bits >= 8)
            {
                if (written == destination.Length)
                {
                    return false;
                }

                destination[written++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }

        return true;
    }
}
