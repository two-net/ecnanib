using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// Seeds the initial templates as version 1 (FR-PRM-04, NFR-DEP-02). A key with any version keeps it, because an
/// administrator may have published one, so a changed seed reaches only a database that has no row for its key.
/// </summary>
internal sealed class PromptTemplateSeeder(TimeProvider time) : IReferenceDataSeeder
{
    public async Task SeedAsync(EcnanibDbContext context, CancellationToken cancellationToken)
    {
        var rows = context.Set<PromptTemplateRow>();
        var existing = (await rows
                .Select(row => new { row.Layer, row.Role, row.Market, row.Timeframe })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Select(row => new PromptTemplateKey(row.Layer, row.Role, row.Market, row.Timeframe))
            .ToHashSet();
        var now = time.GetUtcNow();

        rows.AddRange(PromptSeeds.All
            .Where(seed => !existing.Contains(seed.Key))
            .Select(seed => new PromptTemplateRow
            {
                Id = Guid.CreateVersion7(now),
                Layer = seed.Key.Layer,
                Role = seed.Key.Role,
                Market = seed.Key.Market,
                Timeframe = seed.Key.Timeframe,
                Version = 1,
                Content = seed.Content,
                CreatedAt = now,
            }));
    }
}
