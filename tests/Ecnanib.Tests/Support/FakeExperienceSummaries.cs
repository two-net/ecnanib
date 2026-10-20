using Ecnanib.Council.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>Stands in for the experience module's summaries: scripted per query; records every query.</summary>
internal sealed class FakeExperienceSummaries : IExperienceSummaries
{
    public Func<ExperienceQuery, string?>? OnSummarize { get; set; }

    public List<ExperienceQuery> Queries { get; } = [];

    public Task<string?> SummarizeAsync(ExperienceQuery query, CancellationToken cancellationToken)
    {
        lock (Queries)
            Queries.Add(query);
        return Task.FromResult(OnSummarize?.Invoke(query));
    }
}
