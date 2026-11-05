using Ecnanib.Modules.Administration.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Identity.Contracts;

namespace Ecnanib.Modules.Administration;

/// <summary>
/// Checks that the caller is an administrator, then hands the call to Catalog, which makes the change and
/// audits it. <see cref="ICatalogAdministration"/> checks nobody, which is why only this module may use it.
/// </summary>
internal sealed class CatalogControls(IAdministrators administrators, ICatalogAdministration catalog) : ICatalogControls
{
    public async Task<SyncAllOutcome?> SyncAllAsync(Guid administratorId, CancellationToken cancellationToken) =>
        await IsAllowedAsync(administratorId, cancellationToken) ? await catalog.SyncAllAsync(administratorId, cancellationToken) : null;

    public async Task<IReadOnlyList<SyncRunRecord>?> ListRunsAsync(Guid administratorId, int count, CancellationToken cancellationToken) =>
        await IsAllowedAsync(administratorId, cancellationToken) ? await catalog.ListRunsAsync(count, cancellationToken) : null;

    public async Task<ModelDetail?> GetModelAsync(Guid administratorId, string provider, string modelId, CancellationToken cancellationToken) =>
        await IsAllowedAsync(administratorId, cancellationToken) ? await catalog.GetModelAsync(provider, modelId, cancellationToken) : null;

    public async Task<ModelEditResult?> SaveModelAsync(
        Guid administratorId, string provider, string modelId, ModelEdit edit, CancellationToken cancellationToken) =>
        await IsAllowedAsync(administratorId, cancellationToken)
            ? await catalog.SaveModelAsync(administratorId, provider, modelId, edit, cancellationToken)
            : null;

    public async Task<RoleDefaultsResult?> SaveRoleDefaultsAsync(
        Guid administratorId, IReadOnlyList<RoleDefaultEdit> edits, CancellationToken cancellationToken) =>
        await IsAllowedAsync(administratorId, cancellationToken)
            ? await catalog.SaveRoleDefaultsAsync(administratorId, edits, cancellationToken)
            : null;

    private Task<bool> IsAllowedAsync(Guid userId, CancellationToken cancellationToken) =>
        administrators.IsAdministratorAsync(userId, cancellationToken);
}
