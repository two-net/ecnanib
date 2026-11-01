using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Catalog;

internal sealed class ProviderSeeder : IReferenceDataSeeder
{
    private static readonly string[] Supported = ["Anthropic", "Google", "OpenAI", "xAI"];

    public async Task SeedAsync(EcnanibDbContext context, CancellationToken cancellationToken)
    {
        var providers = context.Set<Provider>();
        var existing = await providers.Select(provider => provider.Name).ToListAsync(cancellationToken);

        providers.AddRange(Supported.Except(existing).Select(name => new Provider { Name = name }));
    }
}
