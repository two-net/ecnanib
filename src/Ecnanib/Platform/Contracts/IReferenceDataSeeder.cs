namespace Ecnanib.Platform.Contracts;

/// <summary>
/// Adds a module's reference data (NFR-DEP-02). Seeders run on every start, after migrations, so
/// an upgrade can add new data. They must add only what's missing, because administrators may have
/// edited existing rows. They must not save: the caller saves once, so seeding is all or nothing.
/// </summary>
public interface IReferenceDataSeeder
{
    Task SeedAsync(EcnanibDbContext context, CancellationToken cancellationToken);
}
