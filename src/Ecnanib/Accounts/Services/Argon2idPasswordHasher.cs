using System.Security.Cryptography;
using System.Text;
using Ecnanib.Database.Entities;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Ecnanib.Accounts.Services;

/// <summary>
/// Argon2id password hashing (NFR-SEC-01) in the PHC string format
/// <c>$argon2id$v=19$m=65536,t=3,p=1$&lt;salt&gt;$&lt;hash&gt;</c>. Verification recomputes with the parameters stored in
/// the string and reports <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> when they differ from the
/// current ones, so raising the cost upgrades every hash at the user's next sign-in. Malformed input never throws:
/// it is simply a failed verification.
/// </summary>
internal sealed class Argon2idPasswordHasher : IPasswordHasher<User>
{
    /// <summary>Memory in KiB (64 MiB), above every OWASP minimum configuration for Argon2id.</summary>
    internal const int MemoryKib = 65_536;

    internal const int Iterations = 3;

    internal const int Parallelism = 1;

    internal const int SaltLength = 16;

    internal const int HashLength = 32;

    private const string Prefix = "$argon2id$v=19$";

    public string HashPassword(User user, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism, HashLength);
        return $"{Prefix}m={MemoryKib},t={Iterations},p={Parallelism}${Encode(salt)}${Encode(hash)}";
    }

    public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
    {
        // An empty password is never stored (the policy requires at least eight characters) and the library rejects it.
        if (string.IsNullOrEmpty(providedPassword) || !TryParse(hashedPassword, out var memoryKib, out var iterations, out var parallelism, out var salt, out var expected))
            return PasswordVerificationResult.Failed;

        var actual = Compute(providedPassword, salt, memoryKib, iterations, parallelism, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return PasswordVerificationResult.Failed;

        var current = memoryKib == MemoryKib && iterations == Iterations && parallelism == Parallelism
            && salt.Length == SaltLength && expected.Length == HashLength;
        return current ? PasswordVerificationResult.Success : PasswordVerificationResult.SuccessRehashNeeded;
    }

    internal static string Format(int memoryKib, int iterations, int parallelism, byte[] salt, byte[] hash) =>
        $"{Prefix}m={memoryKib},t={iterations},p={parallelism}${Encode(salt)}${Encode(hash)}";

    internal static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(length);
    }

    internal static bool TryParse(string? encoded, out int memoryKib, out int iterations, out int parallelism, out byte[] salt, out byte[] hash)
    {
        memoryKib = iterations = parallelism = 0;
        salt = hash = [];
        if (encoded is null || !encoded.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3)
            return false;

        var parameters = parts[0].Split(',');
        if (parameters.Length != 3
            || !TryParameter(parameters[0], "m=", out memoryKib)
            || !TryParameter(parameters[1], "t=", out iterations)
            || !TryParameter(parameters[2], "p=", out parallelism))
            return false;

        if (memoryKib < 8 * parallelism || iterations < 1 || parallelism < 1 || memoryKib > 4 * 1024 * 1024 || iterations > 64 || parallelism > 16)
            return false;

        return TryDecode(parts[1], out salt) && TryDecode(parts[2], out hash) && salt.Length >= 8 && hash.Length >= 16;
    }

    private static bool TryParameter(string text, string name, out int value)
    {
        value = 0;
        return text.StartsWith(name, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(name.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || text.Length % 4 == 1)
            return false;
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '/'))
                return false;
        }

        var padded = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        var buffer = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
            return false;
        bytes = buffer[..written];
        return true;
    }
}
