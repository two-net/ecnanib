using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// A row for each role the RoleDefaults section gives a default (NFR-DEP-02). A role that has a row keeps it,
/// because administrators may have edited it, even to no default, so the section only reaches a role that has
/// none. Runs after <see cref="ProviderSeeder"/>, which the settings' providers are always among.
/// </summary>
internal sealed class RoleDefaultSeeder(RoleDefaultSettings settings) : IReferenceDataSeeder
{
    public async Task SeedAsync(EcnanibDbContext context, CancellationToken cancellationToken)
    {
        var rows = context.Set<RoleDefaultRow>();
        var existing = await rows.Select(row => row.Role).ToListAsync(cancellationToken);

        rows.AddRange(settings.Defaults
            .Where(value => !existing.Contains(value.Role))
            .Select(value => new RoleDefaultRow { Role = value.Role, Provider = value.Provider, ModelId = value.ModelId }));
    }
}
