using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecnanib.Modules.Identity.Contracts;
using NSec.Cryptography;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// NFR-SEC-02: Argon2id through libsodium, whose memory stays off the managed heap of the process that
/// runs trading cycles. A hash is stored in the standard PHC string format with its own parameters,
/// so these can be raised later without touching existing hashes.
/// </summary>
internal sealed class Argon2PasswordHasher : IPasswordHasher, IDisposable
{
    private const int MemoryKibibytes = 64 * 1024;
    private const int Passes = 3;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Each hash holds its full memory while it runs, so this caps the total at 256 MiB.</summary>
    private const int ConcurrentHashes = 4;

    private const string Prefix = "argon2id";
    private const string Version = "v=19";
    private const char Separator = '$';

    private static readonly Argon2Parameters Current = new()
    {
        MemorySize = MemoryKibibytes,
        NumberOfPasses = Passes,
        DegreeOfParallelism = Parallelism,
    };

    private readonly SemaphoreSlim _slots = new(ConcurrentHashes);

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(password, salt, Current, HashBytes, cancellationToken);

        return string.Join(
            Separator,
            string.Empty,
            Prefix,
            Version,
            string.Create(CultureInfo.InvariantCulture, $"m={Current.MemorySize},t={Current.NumberOfPasses},p={Current.DegreeOfParallelism}"),
            ToBase64(salt),
            ToBase64(hash));
    }

    public async Task<bool> VerifyAsync(string password, string hash, CancellationToken cancellationToken)
    {
        var parts = hash.Split(Separator);

        if (parts is not [_, Prefix, Version, var settings, var salt, var expected])
        {
            throw new FormatException("A stored password hash isn't an Argon2id PHC string.");
        }

        var expectedBytes = FromBase64(expected);
        var actual = await DeriveAsync(password, FromBase64(salt), ParseParameters(settings), expectedBytes.Length, cancellationToken);

        return CryptographicOperations.FixedTimeEquals(actual, expectedBytes);
    }

    public Task SpendAsync(string password, CancellationToken cancellationToken) =>
        DeriveAsync(password, new byte[SaltBytes], Current, HashBytes, cancellationToken);

    public void Dispose() => _slots.Dispose();

    private async Task<byte[]> DeriveAsync(
        string password,
        byte[] salt,
        Argon2Parameters parameters,
        int length,
        CancellationToken cancellationToken)
    {
        // Bytes, because the overload that takes a string hashes UTF-16, which no other Argon2
        // implementation would, so these hashes could never move to another library.
        var bytes = Encoding.UTF8.GetBytes(password);

        await _slots.WaitAsync(cancellationToken);

        try
        {
            // DeriveBytes blocks a thread for the whole hash.
            return await Task.Run(
                () => PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveBytes(bytes, salt, length),
                cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    private static Argon2Parameters ParseParameters(string settings)
    {
        var values = settings.Split(',').Select(setting => setting.Split('=')).ToDictionary(pair => pair[0], pair => pair[1]);

        return new Argon2Parameters
        {
            MemorySize = long.Parse(values["m"], CultureInfo.InvariantCulture),
            NumberOfPasses = long.Parse(values["t"], CultureInfo.InvariantCulture),
            DegreeOfParallelism = int.Parse(values["p"], CultureInfo.InvariantCulture),
        };
    }

    /// <summary>PHC strings leave out the padding.</summary>
    private static string ToBase64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromBase64(string text) =>
        Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
}
