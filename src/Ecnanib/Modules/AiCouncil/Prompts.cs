using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// Reads the templates that could apply to a combination and composes them. There's no cache: a cycle composes a
/// handful of prompts from a small table, and an administrator's new version applies to the next prompt (UC-08).
/// </summary>
internal sealed class Prompts(IDbContextFactory<EcnanibDbContext> contexts) : IPrompts
{
    public async Task<ComposedPrompt> ComposeAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await context.Set<PromptTemplateRow>()
            .AsNoTracking()
            .Where(row => (row.Role == null || row.Role == request.Role)
                && (row.Market == null || row.Market == request.Market)
                && (row.Timeframe == null || row.Timeframe == request.Timeframe))
            .ToListAsync(cancellationToken);

        return PromptComposer.Compose(rows.Select(row => row.ToTemplate()), request);
    }
}
