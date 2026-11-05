using Ecnanib.Kernel;

namespace Ecnanib.Modules.Catalog.Contracts;

/// <summary>
/// Administrators' controls over the catalog (FR-CAT-03, FR-CAT-05, FR-ADM-03): syncing every user's keys,
/// overriding a model's metadata, and editing the roles' defaults. Only Catalog and Administration may use
/// this, which BannedApis enforces, because nothing here checks who's asking: Administration's
/// <c>ICatalogControls</c> does. Each change is audited, as the administrator passed in, in the transaction
/// that makes it. See Administration in docs/architecture.md.
/// </summary>
public interface ICatalogAdministration
{
    /// <summary>
    /// Starts a sync of every user's valid AI keys in the background, unless one is running, and audits it
    /// when it starts.
    /// </summary>
    Task<SyncAllOutcome> SyncAllAsync(Guid administratorId, CancellationToken cancellationToken);

    /// <summary>The newest sync runs, one row per provider per run (FR-CAT-08), newest first.</summary>
    Task<IReadOnlyList<SyncRunRecord>> ListRunsAsync(int count, CancellationToken cancellationToken);

    /// <summary>A model, with what each layer says about the fields an administrator can override, or null.</summary>
    Task<ModelDetail?> GetModelAsync(string provider, string modelId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a model's overrides (FR-CAT-05). They're stored apart from what providers report, so they
    /// persist across syncs. An edit that sets nothing removes them.
    /// </summary>
    Task<ModelEditResult> SaveModelAsync(Guid administratorId, string provider, string modelId, ModelEdit edit, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the roles' defaults (FR-ROLE-03), in one transaction, with an audit record for each role that
    /// changed. A role that isn't in <paramref name="edits"/> keeps its default.
    /// </summary>
    Task<RoleDefaultsResult> SaveRoleDefaultsAsync(Guid administratorId, IReadOnlyList<RoleDefaultEdit> edits, CancellationToken cancellationToken);
}

/// <summary>
/// What an administrator sets on a model (FR-CAT-05). A null field, and a null capability, inherits from the
/// configuration and then the provider.
/// </summary>
/// <param name="Price">Both prices or neither, because a cost needs both.</param>
/// <param name="RecommendedRoles">Not inherited: providers and the configuration don't recommend.</param>
public sealed record ModelEdit(
    string? DisplayName,
    bool Hidden,
    ModelCapabilities Capabilities,
    ModelPrice? Price,
    IReadOnlyCollection<AiRole> RecommendedRoles)
{
    public static readonly ModelEdit None = new(null, false, ModelCapabilities.Unknown, null, []);

    public bool IsNone =>
        DisplayName is null && !Hidden && Capabilities == ModelCapabilities.Unknown && Price is null && RecommendedRoles.Count == 0;
}

/// <summary>What one source says about the fields an administrator can override.</summary>
/// <param name="DisplayName">Null when the source doesn't name it.</param>
public sealed record ModelLayer(string? DisplayName, ModelCapabilities Capabilities, ModelPrice? Price);

/// <param name="Model">As everyone sees it, with every layer applied.</param>
/// <param name="Reported">What providers reported. They give no prices that Ecnanib reads.</param>
/// <param name="Configured">The Catalog:Overrides entry for it, or null.</param>
/// <param name="Overridden">Administrators' overrides, or <see cref="ModelEdit.None"/>.</param>
public sealed record ModelDetail(CatalogModel Model, ModelLayer Reported, ModelLayer? Configured, ModelEdit Overridden);

public enum ModelEditOutcome
{
    Saved,

    /// <summary>No sync has listed the model.</summary>
    NotFound,

    /// <summary>A field is invalid. Nothing changed.</summary>
    Invalid,
}

/// <summary>The fields of a <see cref="ModelEdit"/> that can be invalid.</summary>
public enum ModelEditField
{
    /// <summary>Longer than 100 characters, or with a control character.</summary>
    DisplayName,

    /// <summary>Not from 0 to 1,000,000.</summary>
    InputPrice,

    /// <summary>Not from 0 to 1,000,000.</summary>
    OutputPrice,
}

public sealed record ModelEditResult(ModelEditOutcome Outcome, IReadOnlyList<ModelEditField> Problems)
{
    public static readonly ModelEditResult Saved = new(ModelEditOutcome.Saved, []);

    public static readonly ModelEditResult NotFound = new(ModelEditOutcome.NotFound, []);
}

/// <summary>A role's new default.</summary>
/// <param name="Provider">An AI provider's name, or null for no default, which a restart doesn't undo.</param>
/// <param name="ModelId">One model's exact ID, or null for the provider's best that the user can use.</param>
public sealed record RoleDefaultEdit(AiRole Role, string? Provider, string? ModelId);

public enum RoleDefaultProblem
{
    /// <summary>No adapter has that name.</summary>
    UnknownProvider,

    /// <summary>Not one exact ID, of at most 256 characters, with no spaces.</summary>
    InvalidModel,

    /// <summary>A model with no provider.</summary>
    ModelWithoutProvider,
}

/// <param name="Problems">By role. Nothing changed when there are any.</param>
public sealed record RoleDefaultsResult(IReadOnlyDictionary<AiRole, RoleDefaultProblem> Problems)
{
    public bool Saved => Problems.Count == 0;
}

/// <summary>One provider's part of a sync (FR-CAT-08).</summary>
/// <param name="FinishedAt">Null when it was cut short, or is still running.</param>
/// <param name="Keys">The valid keys it asked.</param>
/// <param name="KeysFailed">The keys the provider didn't answer for.</param>
/// <param name="Errors">How many errors it recorded, one per failed key.</param>
public sealed record SyncRunRecord(
    long Id,
    string Provider,
    CatalogSyncTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int Keys,
    int KeysFailed,
    int ModelsAdded,
    int ModelsUpdated,
    int ModelsDeprecated,
    int ModelsRetired,
    int Errors);

/// <summary>
/// The field names in the catalog's audit summaries (FR-AUD-02). Prefixed, so none clashes with another
/// module's or looks like a secret's name. Records store them, so a value is never renamed.
/// </summary>
public static class CatalogAuditFields
{
    public const string Provider = "Catalog.Provider";

    public const string Model = "Catalog.Model";

    public const string DisplayName = "Catalog.DisplayName";

    /// <summary><c>True</c> or <c>False</c>.</summary>
    public const string Hidden = "Catalog.Hidden";

    /// <summary>
    /// A capability's override: <c>True</c>, <c>False</c>, or none for inherited. The field is this
    /// prefix and a <see cref="ModelCapability"/> name.
    /// </summary>
    public const string CapabilityPrefix = "Catalog.Capability.";

    public const string InputPrice = "Catalog.InputPrice";

    public const string OutputPrice = "Catalog.OutputPrice";

    /// <summary><see cref="AiRole"/> names, comma separated.</summary>
    public const string RecommendedRoles = "Catalog.RecommendedRoles";

    public const string DefaultProvider = "Catalog.DefaultProvider";

    public const string DefaultModel = "Catalog.DefaultModel";
}
