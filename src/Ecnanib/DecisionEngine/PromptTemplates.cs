using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Reads the prompt templates that are in use (FR-PRM-7). Nothing is written: a template arrives
/// with a migration, and no other way (NFR-DAT-10).
/// </summary>
internal sealed class PromptTemplates(IDbContextFactory<EcnanibDbContext> contexts) : IPromptTemplates
{
    /// <summary>The subject of the one profile that the product has (FR-RSK-1).</summary>
    private const string AggressiveProfile = "Aggressive";

    public async Task<TemplatesInUse> InUseAsync(Market market, Timeframe timeframe, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Every template that is in use: one for each kind and subject, which the database holds
        // to with an index. They are a handful of rows, and an agent uses most of them.
        var inUse = await context.Set<PromptTemplate>()
            .AsNoTracking()
            .Where(template => template.IsActive)
            .ToListAsync(cancellationToken);

        return new TemplatesInUse(
            Enum.GetValues<Role>().ToDictionary(role => role, role => Find(PromptTemplateKind.Role, role.ToString())),
            Find(PromptTemplateKind.Market, market.ToString()),
            Find(PromptTemplateKind.Timeframe, timeframe.Code()),
            Find(PromptTemplateKind.Profile, AggressiveProfile),
            Find(PromptTemplateKind.Lesson, PromptTemplateConfiguration.LessonSubject));

        // A role and a market are stored under their names, and a timeframe under its code, as
        // the seed names them.
        PromptTemplate Find(PromptTemplateKind kind, string subject) =>
            inUse.SingleOrDefault(template => template.Kind == kind && template.Subject == subject)
            ?? throw new InvalidOperationException(
                $"No prompt template is in use for the {kind} {subject}. Templates are seeded through migrations, " +
                "one in use for each role, market, timeframe, and profile, and one for the lesson. See docs/prompts.md.");
    }
}
