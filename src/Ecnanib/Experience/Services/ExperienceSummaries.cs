using Ecnanib.Council.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Experience.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecnanib.Experience.Services;

/// <summary>
/// The experience summary each role prompt carries (FR-AI-06, FR-EXP-06): the headline statistics at the three
/// scopes from the last refresh, and the most recent outcomes read live, so a trade closed a minute ago is already
/// in the next prompt. Null when nothing is recorded; the council logs a failure and runs the role without it.
/// </summary>
internal sealed class ExperienceSummaries : IExperienceSummaries
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<ExperienceOptions> _options;

    public ExperienceSummaries(IDbContextFactory<EcnanibDbContext> factory, IOptions<ExperienceOptions> options)
    {
        _factory = factory;
        _options = options;
    }

    public async Task<string?> SummarizeAsync(ExperienceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var recent = _options.Value.RecentOutcomes;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var stats = await db.ExperienceStats.AsNoTracking()
            .Where(s => s.Provider == query.Provider && s.Model == query.Model && s.Role == query.Role && s.Timeframe == query.Timeframe && s.MarketType == query.MarketType
                        && (s.Scope == StatScope.System
                            || (s.Scope == StatScope.User && s.UserId == query.UserId)
                            || (s.Scope == StatScope.Agent && s.UserId == query.UserId && s.AgentId == query.AgentId)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var trades = await db.Trades.AsNoTracking()
            .Where(t => t.ClosedAt != null && t.RMultiple != null && t.Agent.UserId == query.UserId && t.Timeframe == query.Timeframe && t.MarketType == query.MarketType)
            .SelectMany(t => t.EntryDecision.RoleOutputs.Where(r => r.Provider == query.Provider && r.Model == query.Model && r.Role == query.Role && r.Status == RoleOutputStatus.Succeeded),
                (t, r) => new { t.ClosedAt, t.RMultiple, t.ExitReason, r.ProposedAction })
            .OrderByDescending(x => x.ClosedAt)
            .Take(recent)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var rejections = await db.ShadowOutcomes.AsNoTracking()
            .Where(s => s.Result != ShadowResult.Pending && s.ResolvedAt != null
                        && s.Decision.Evaluation.Agent.UserId == query.UserId && s.Decision.Evaluation.Timeframe == query.Timeframe && s.Decision.Evaluation.MarketType == query.MarketType
                        && s.Decision.RoleOutputs.Any(r => r.Provider == query.Provider && r.Model == query.Model && r.Role == query.Role && r.Status == RoleOutputStatus.Succeeded && r.ProposedAction == DecisionAction.NoTrade))
            .OrderByDescending(s => s.ResolvedAt)
            .Take(recent)
            .Select(s => new { s.Result, s.RMultiple, s.Detail, At = s.Decision.Evaluation.CandleOpenTime })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return ExperienceSummaryText.Compose(
            query,
            stats.FirstOrDefault(s => s.Scope == StatScope.Agent)?.Metrics,
            stats.FirstOrDefault(s => s.Scope == StatScope.User)?.Metrics,
            stats.FirstOrDefault(s => s.Scope == StatScope.System)?.Metrics,
            trades.Select(t => new RecentTrade(t.ClosedAt!.Value, t.RMultiple!.Value, t.ExitReason, t.ProposedAction == DecisionAction.NoTrade)).ToList(),
            rejections.Select(r => new RecentRejection(r.At, r.Result, r.RMultiple, ShadowDetail.Parse(r.Detail)?.Resolution)).ToList(),
            recent);
    }
}
