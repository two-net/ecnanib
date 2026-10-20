using Ecnanib.Council.Abstractions;

namespace Ecnanib.Council.Services.Placeholders;

/// <summary>No experience until the experience issue (FR-EXP, #11) delivers the statistics; prompts say so (FR-EXP-06).</summary>
internal sealed class NoExperienceSummaries : IExperienceSummaries
{
    public Task<string?> SummarizeAsync(ExperienceQuery query, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
