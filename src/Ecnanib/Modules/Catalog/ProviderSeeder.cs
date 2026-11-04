using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// A row for each provider with an adapter, so adding a provider needs no change here (NFR-MNT-02). Rows are
/// never removed, because models refer to them.
/// </summary>
internal sealed class ProviderSeeder(IAiProviders providers) : IReferenceDataSeeder
{
    public async Task SeedAsync(EcnanibDbContext context, CancellationToken cancellationToken)
    {
        var rows = context.Set<Provider>();
        var existing = await rows.Select(provider => provider.Name).ToListAsync(cancellationToken);

        rows.AddRange(providers.All
            .Select(provider => provider.Name)
            .Except(existing, StringComparer.Ordinal)
            .Select(name => new Provider { Name = name }));
    }
}
