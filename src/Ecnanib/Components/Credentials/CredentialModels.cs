using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Components.Credentials;

/// <summary>What one AI key could use at its last sync (FR-CAT-02), for its card.</summary>
/// <param name="SyncedAt">When that sync began asking the provider.</param>
public sealed record CredentialModels(int Available, DateTimeOffset SyncedAt)
{
    /// <summary>One for each key that has been synced. A key that never was has none.</summary>
    public static Dictionary<Guid, CredentialModels> Of(IEnumerable<ModelAccess> access) =>
        access
            .GroupBy(row => row.CredentialId)
            .ToDictionary(key => key.Key, key => new CredentialModels(key.Count(row => row.Available), key.Max(row => row.CheckedAt)));
}
