using Ecnanib.Modules.Catalog.Contracts;

namespace Ecnanib.Modules.Administration.Contracts;

/// <summary>
/// Administrators' controls over the model catalog and the roles' defaults (FR-ADM-03, FR-CAT-03, FR-CAT-05).
/// Every call checks that <c>administratorId</c> is an administrator now, because a circuit has no middleware,
/// and answers null when they aren't, having done nothing. Reading the catalog itself needs no check, so pages
/// use <see cref="ICatalog"/> for that. See Administration in docs/architecture.md.
/// </summary>
public interface ICatalogControls
{
    /// <summary>Starts a sync of every user's valid AI keys, unless one is running, and audits it.</summary>
    Task<SyncAllOutcome?> SyncAllAsync(Guid administratorId, CancellationToken cancellationToken);

    /// <summary>The newest sync runs, one row per provider per run (FR-CAT-08), newest first.</summary>
    Task<IReadOnlyList<SyncRunRecord>?> ListRunsAsync(Guid administratorId, int count, CancellationToken cancellationToken);

    /// <summary>A model with each layer of its metadata. Null also when no sync has listed it.</summary>
    Task<ModelDetail?> GetModelAsync(Guid administratorId, string provider, string modelId, CancellationToken cancellationToken);

    /// <summary>Replaces a model's overrides (FR-CAT-05), and audits the change.</summary>
    Task<ModelEditResult?> SaveModelAsync(
        Guid administratorId, string provider, string modelId, ModelEdit edit, CancellationToken cancellationToken);

    /// <summary>Sets the roles' defaults (FR-ROLE-03), and audits each change.</summary>
    Task<RoleDefaultsResult?> SaveRoleDefaultsAsync(
        Guid administratorId, IReadOnlyList<RoleDefaultEdit> edits, CancellationToken cancellationToken);
}
