using System.Security.Cryptography;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Secrets;

/// <summary>Master keys for tests, as the settings an application reads them from.</summary>
internal static class TestKeys
{
    public const string Section = "Security:EncryptionKeys";

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Security:EncryptionKeys with these entries, in order.</summary>
    public static Dictionary<string, string?> Settings(params (string Id, string Key, bool Active)[] keys)
    {
        var settings = new Dictionary<string, string?>();

        foreach (var (index, key) in keys.Index())
        {
            settings[$"{Section}:{index}:Id"] = key.Id;
            settings[$"{Section}:{index}:Key"] = key.Key;
            settings[$"{Section}:{index}:Active"] = key.Active ? "true" : "false";
        }

        return settings;
    }

    /// <summary>The default test key, plus <paramref name="extra"/>, for a test that needs both.</summary>
    public static Dictionary<string, string?> WithDefault(Dictionary<string, string?> extra)
    {
        var settings = Settings((AppFactory.TestKeyId, AppFactory.TestKey, true));

        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }
}
