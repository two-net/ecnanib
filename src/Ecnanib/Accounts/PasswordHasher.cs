using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Ecnanib.Accounts;

/// <summary>The rules a new password must meet. There are no composition rules: length is what counts.</summary>
public static class PasswordRules
{
    /// <summary>The fewest characters a password may have.</summary>
    public const int MinimumLength = 12;

    /// <summary>The most characters a password may have, so that hashing one has a bounded cost.</summary>
    public const int MaximumLength = 128;
}

/// <summary>
/// Turns a password into the only form it is stored in: a salted hash from Argon2id, a
/// memory-hard algorithm (NFR-SEC-2). See docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// A hash is text in the PHC format, <c>$argon2id$v=19$m=19456,t=2,p=1$salt$hash</c>. It names its
/// own parameters, so they can be raised later: a hash made with earlier ones still verifies.
/// </para>
/// <para>
/// A password is brought to one Unicode form (NFKC) first, so that the same password typed on
/// another keyboard or device gives the same hash.
/// </para>
/// </remarks>
public sealed class PasswordHasher
{
    private const string Algorithm = "argon2id";

    // The version of Argon2 that the library implements: 1.3, written as 0x13.
    private const int Version = 19;

    private const int SaltLength = 16;
    private const int HashLength = 32;

    // The parameters of new hashes: 19 MiB of memory, two passes, one lane. This is the smallest
    // configuration that OWASP's password storage guidance recommends for Argon2id.
    private static readonly Parameters Current = new(MemoryKib: 19_456, Passes: 2, Lanes: 1);

    // Each hash holds its memory for as long as it runs, and more of them at once than there are
    // processors would only add memory, not speed.
    private readonly SemaphoreSlim _running = new(Environment.ProcessorCount);

    private readonly record struct Parameters(int MemoryKib, int Passes, int Lanes);

    /// <summary>Hashes a password with a salt of its own.</summary>
    public async Task<string> HashAsync(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = await DeriveAsync(password, salt, Current, HashLength);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"${Algorithm}$v={Version}$m={Current.MemoryKib},t={Current.Passes},p={Current.Lanes}${Encode(salt)}${Encode(hash)}");
    }

    /// <summary>
    /// True when <paramref name="password"/> is the password that <paramref name="hash"/> was made
    /// from. A text that is not a hash of this kind matches no password.
    /// </summary>
    public async Task<bool> VerifyAsync(string password, string hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hash);

        if (!TryParse(hash, out var parameters, out var salt, out var expected))
        {
            return false;
        }

        var actual = await DeriveAsync(password, salt, parameters, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private async Task<byte[]> DeriveAsync(string password, byte[] salt, Parameters parameters, int length)
    {
        var bytes = Encoding.UTF8.GetBytes(InOneForm(password));

        await _running.WaitAsync();
        try
        {
            using var argon2 = new Argon2id(bytes)
            {
                Salt = salt,
                MemorySize = parameters.MemoryKib,
                Iterations = parameters.Passes,
                DegreeOfParallelism = parameters.Lanes,
            };

            return await argon2.GetBytesAsync(length);
        }
        finally
        {
            _running.Release();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string InOneForm(string password)
    {
        try
        {
            return password.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Text that is not valid Unicode has no other form. It is hashed as it is.
            return password;
        }
    }

    private static bool TryParse(string hash, out Parameters parameters, out byte[] salt, out byte[] expected)
    {
        parameters = default;
        salt = [];
        expected = [];

        // "$argon2id$v=19$m=19456,t=2,p=1$salt$hash" starts with an empty part.
        var parts = hash.Split('$');
        if (parts is not ["", Algorithm, var version, var costs, var saltText, var hashText]
            || version != string.Create(CultureInfo.InvariantCulture, $"v={Version}"))
        {
            return false;
        }

        var cost = costs.Split(',');
        if (cost.Length != 3
            || !TryCost(cost[0], "m=", out var memoryKib)
            || !TryCost(cost[1], "t=", out var passes)
            || !TryCost(cost[2], "p=", out var lanes)
            || !TryDecode(saltText, out salt)
            || !TryDecode(hashText, out expected)
            || salt.Length < 8
            || expected.Length < 16)
        {
            return false;
        }

        parameters = new Parameters(memoryKib, passes, lanes);
        return true;
    }

    private static bool TryCost(string text, string name, out int value)
    {
        value = 0;

        return text.StartsWith(name, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(name.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }

    // The PHC format writes Base64 without padding.
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryDecode(string text, out byte[] bytes)
    {
        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
        bytes = new byte[padded.Length];

        if (!Convert.TryFromBase64String(padded, bytes, out var length))
        {
            bytes = [];
            return false;
        }

        bytes = bytes[..length];
        return true;
    }
}
