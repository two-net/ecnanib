namespace Ecnanib.Modules.Catalog.Contracts;

/// <summary>A user's sync of their own keys, on demand (FR-CAT-03).</summary>
public interface ICatalogSync
{
    /// <summary>
    /// Lists the models of each of the user's valid AI keys and merges them into the catalog, then
    /// returns once it's done. At most once a minute per user, because hub traffic never reaches the rate
    /// limiter.
    /// </summary>
    Task<UserSyncResult> SyncUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <param name="Keys">The user's valid AI keys it asked about.</param>
/// <param name="KeysFailed">How many of them the provider didn't answer for.</param>
/// <param name="Models">How many models the keys that answered can use between them.</param>
public sealed record UserSyncResult(UserSyncOutcome Outcome, int Keys = 0, int KeysFailed = 0, int Models = 0);

public enum UserSyncOutcome
{
    /// <summary>It ran. Some keys may have failed: see <see cref="UserSyncResult.KeysFailed"/>.</summary>
    Synced,

    /// <summary>The user has no valid AI key of an enabled provider.</summary>
    NoKeys,

    /// <summary>The user synced less than a minute ago.</summary>
    TooSoon,

    /// <summary>The run didn't finish, such as when it timed out or the application was stopping.</summary>
    Failed,
}

public enum SyncAllOutcome
{
    Started,

    /// <summary>A sync of every key is already running, scheduled or on demand.</summary>
    AlreadyRunning,
}

/// <summary>What started a sync run (FR-CAT-03, FR-CAT-08). Runs store the name, so a value is never renamed.</summary>
public enum CatalogSyncTrigger
{
    /// <summary>Catalog:SyncIntervalHours came round.</summary>
    Schedule,

    /// <summary>An administrator asked for every key.</summary>
    Administrator,

    /// <summary>A key was added or replaced, or a check found it valid again.</summary>
    Credential,

    /// <summary>A user asked for their own keys.</summary>
    User,
}
