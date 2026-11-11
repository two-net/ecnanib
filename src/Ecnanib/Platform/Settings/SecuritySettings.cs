namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Security</c> section: the master encryption keys and the session lifetime.</summary>
/// <param name="ActiveKeyId">The id of the master key that encrypts new values (FR-KEY-7).</param>
/// <param name="MasterKeys">
/// The master keys by id, each 32 bytes in Base64. Older keys stay listed so the values they
/// encrypted can still be read until they are re-encrypted. Ids ignore case.
/// </param>
/// <param name="SessionIdleMinutes">How long a session lasts without activity (NFR-SEC-1).</param>
public sealed record SecuritySettings(
    string ActiveKeyId,
    IReadOnlyDictionary<string, Secret> MasterKeys,
    int SessionIdleMinutes)
{
    internal static SecuritySettings Read(SettingsReader file)
    {
        const string activeKey = "Security:ActiveKeyId";
        const string keysKey = "Security:MasterKeys";

        var masterKeys = new Dictionary<string, Secret>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in file.Map(keysKey, emptyRule: "must hold at least one master key"))
        {
            masterKeys[id] = file.MasterKey(SettingsReader.Join(keysKey, id));
        }

        var activeKeyId = file.Text(activeKey);
        if (activeKeyId.Length > 0 && masterKeys.Count > 0 && !masterKeys.ContainsKey(activeKeyId))
        {
            file.Problem(activeKey, $"must be the id of one of the keys in {keysKey}");
        }

        var sessionIdleMinutes = file.WholeNumber("Security:SessionIdleMinutes", minimum: 1, maximum: 1440);

        return new SecuritySettings(activeKeyId, masterKeys, sessionIdleMinutes);
    }
}
