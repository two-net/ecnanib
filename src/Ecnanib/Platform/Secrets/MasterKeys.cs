using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Ecnanib.Platform.Secrets;

/// <summary>One configured master key. The bytes are pinned, and live as long as the process.</summary>
internal sealed class MasterKey(string id, byte[] bytes)
{
    public string Id { get; } = id;

    public byte[] Bytes { get; } = bytes;
}

/// <summary>
/// The master keys in Security:EncryptionKeys (FR-KEY-07, NFR-SEC-05). Read() is the only way in, and
/// it reads that section and nothing else in Security, which other issues own. A missing or invalid
/// key stops startup with a message that names the entry (CFG-03).
/// </summary>
/// <remarks>
/// Messages say "Security:EncryptionKeys, entry 0" rather than the key's path. The masking rules
/// would hide whatever follows "EncryptionKeys:", and these messages reach stderr through them.
/// </remarks>
internal sealed partial class MasterKeys
{
    public const string Section = "Security:EncryptionKeys";

    /// <summary>Key IDs are stored with each secret, and shown in logs and to administrators.</summary>
    public const int KeyIdMaxLength = 32;

    /// <summary>AES-256 (FR-KEY-07).</summary>
    public const int KeyLength = 32;

    /// <summary>The value in SRS 7.3. It isn't base64 either, but it's refused by name so the message says so.</summary>
    public const string SampleKey = "<base64-encoded 256-bit key>";

    private const string Generate = "Generate one with: openssl rand -base64 32";

    private static readonly string[] EntrySettings = ["Id", "Key", "Active"];

    private readonly Dictionary<string, MasterKey> _byId;

    private MasterKeys(MasterKey active, Dictionary<string, MasterKey> byId)
    {
        Active = active;
        _byId = byId;
    }

    /// <summary>The key that encrypts every new secret.</summary>
    public MasterKey Active { get; }

    public IEnumerable<string> Ids => _byId.Keys;

    public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MasterKey? key) =>
        _byId.TryGetValue(id, out key);

    /// <exception cref="InvalidOperationException">A key is missing or invalid. The message names the entry.</exception>
    public static MasterKeys Read(IConfiguration configuration)
    {
        Require(
            AesGcm.IsSupported,
            "AES-GCM isn't available on this platform, so secrets can't be encrypted (FR-KEY-07).");

        var entries = configuration.GetSection(Section).GetChildren().ToList();

        Require(entries.Count > 0, $"{Section} needs at least one key, marked Active. {Generate}");
        Require(
            entries.All(entry => int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)),
            $"{Section} must be a list of entries, each with an Id, a Key and Active.");

        var keys = new List<Entry>();

        foreach (var section in entries)
        {
            var entry = ReadEntry(section);

            if (keys.Find(other => string.Equals(other.Key.Id, entry.Key.Id, StringComparison.OrdinalIgnoreCase)) is { } sameId)
            {
                Fail($"{Section}, entries {sameId.Index} and {entry.Index}, have the same Id. Each key needs its own.");
            }

            if (keys.Find(other => CryptographicOperations.FixedTimeEquals(other.Key.Bytes, entry.Key.Bytes)) is { } sameKey)
            {
                Fail($"{Section}, entries {sameKey.Index} and {entry.Index}, have the same Key. {Generate}");
            }

            keys.Add(entry);
        }

        var activeKeys = keys.FindAll(key => key.Active);

        Require(
            activeKeys.Count == 1,
            $"{Section} must mark exactly one key Active, and it marks {activeKeys.Count}. "
            + "The active key encrypts new secrets, and the others only decrypt until re-encryption has finished.");

        return new MasterKeys(
            activeKeys[0].Key,
            keys.ToDictionary(key => key.Key.Id, key => key.Key, StringComparer.Ordinal));
    }

    private static Entry ReadEntry(IConfigurationSection entry)
    {
        var name = $"{Section}, entry {entry.Key}";

        foreach (var setting in entry.GetChildren())
        {
            Require(
                EntrySettings.Contains(setting.Key, StringComparer.OrdinalIgnoreCase),
                $"{name}, has a setting called \"{setting.Key}\", which isn't one. Each entry has an Id, a Key and Active.");
        }

        var id = entry["Id"];

        Require(!string.IsNullOrWhiteSpace(id), $"{name}, needs an Id, such as the month the key was made, like 2026-11.");
        Require(
            KeyIdPattern().IsMatch(id!),
            $"{name}, has an Id that isn't 1 to {KeyIdMaxLength} letters, digits, dots, hyphens or underscores.");

        name = $"{name} (Id \"{id}\")";

        var active = false;
        var activeText = entry["Active"];

        Require(
            activeText is null || bool.TryParse(activeText, out active),
            $"{name}, must have Active set to true or false.");

        return new Entry(entry.Key, new MasterKey(id!, ReadKey(entry["Key"], name)), active);
    }

    private static byte[] ReadKey(string? text, string name)
    {
        Require(!string.IsNullOrWhiteSpace(text), $"{name}, needs a Key. {Generate}");
        Require(text != SampleKey, $"{name}, has the sample Key from the documentation. {Generate}");

        var decoded = new byte[(text!.Length * 3 / 4) + 3];

        try
        {
            Require(
                Convert.TryFromBase64String(text, decoded, out var length) && length == KeyLength,
                $"{name}, needs a Key of {KeyLength} random bytes in base64, which is 44 characters. {Generate}");
            Require(
                decoded.AsSpan(1, KeyLength - 1).ContainsAnyExcept(decoded[0]),
                $"{name}, has a Key that isn't random. {Generate}");

            var key = GC.AllocateUninitializedArray<byte>(KeyLength, pinned: true);
            decoded.AsSpan(0, KeyLength).CopyTo(key);

            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            Fail(message);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new InvalidOperationException(message);

    /// <param name="Index">The entry's position in the section, which is what messages name.</param>
    private sealed record Entry(string Index, MasterKey Key, bool Active);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,32}\z")]
    private static partial Regex KeyIdPattern();
}
