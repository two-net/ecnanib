using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Experience.Services;

/// <summary>Reads the stored statistics for the pages (FR-EXP-07), re-aggregating the counts and sums over whatever the filter leaves open.</summary>
internal sealed class ExperienceReader : IExperienceReader
{
    private const int RecentTrades = 20;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public ExperienceReader(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<LeaderboardView> GetLeaderboardAsync(Guid userId, ExperienceFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ExperienceStats.AsNoTracking()
            .Where(s => s.Scope == StatScope.System || (s.Scope == StatScope.User && s.UserId == userId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var matching = rows.Where(filter.Matches).ToList();
        return new LeaderboardView(
            Rank(matching.Where(s => s.Scope == StatScope.User)),
            Rank(matching.Where(s => s.Scope == StatScope.System)),
            ComputedAt(rows));
    }

    public async Task<ProviderExperienceView> GetProviderAsync(Guid userId, AiProvider provider, ExperienceFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ExperienceStats.AsNoTracking()
            .Where(s => s.Provider == provider && (s.Scope == StatScope.System || s.UserId == userId))
            .Include(s => s.Agent)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var matching = rows.Where(filter.Matches).ToList();
        var agents = matching
            .Where(s => s.Scope == StatScope.Agent && s.Agent is not null)
            .OrderBy(s => s.Agent!.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Role).ThenBy(s => s.Model, StringComparer.Ordinal).ThenBy(s => s.Timeframe).ThenBy(s => s.MarketType)
            .Select(s => new AgentExperienceRow(s.AgentId!.Value, s.Agent!.Name, s.Model, s.Role, s.Timeframe, s.MarketType, s.Metrics))
            .ToList();
        return new ProviderExperienceView(provider, ByModelAndRole(matching.Where(s => s.Scope == StatScope.User)), ByModelAndRole(matching.Where(s => s.Scope == StatScope.System)), agents, ComputedAt(rows));
    }

    public async Task<AgentExperienceView?> GetAgentAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var assignments = await db.CouncilAssignments.AsNoTracking()
            .Where(c => c.AgentId == agentId && c.Agent.UserId == userId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (assignments.Count == 0 && !await db.Agents.AnyAsync(a => a.Id == agentId && a.UserId == userId, cancellationToken).ConfigureAwait(false))
            return null;

        var rows = await db.ExperienceStats.AsNoTracking()
            .Where(s => s.Scope == StatScope.Agent && s.AgentId == agentId && s.UserId == userId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var roles = rows
            .OrderBy(s => s.Role).ThenBy(s => s.Provider).ThenBy(s => s.Model, StringComparer.Ordinal).ThenBy(s => s.Timeframe).ThenBy(s => s.MarketType)
            .Select(s => new RoleExperienceRow(s.Role, s.Provider, s.Model, s.Timeframe, s.MarketType,
                assignments.Any(c => c.Role == s.Role && c.Provider == s.Provider && c.Model == s.Model), s.Metrics))
            .ToList();

        var trades = await db.Trades.AsNoTracking()
            .Where(t => t.AgentId == agentId && t.Agent.UserId == userId && t.ClosedAt != null)
            .OrderByDescending(t => t.ClosedAt)
            .Take(RecentTrades)
            .Select(t => new TradeOutcomeView(t.Id, t.EntryDecision.EvaluationId, t.Side, t.EntryPrice, t.ExitPrice, t.Size, t.RMultiple, t.RealizedPnl, t.Fees, t.Funding,
                t.MaxAdverseExcursion, t.MaxFavorableExcursion, t.ExitReason, t.IsPaper, t.OpenedAt, t.ClosedAt!.Value,
                db.Decisions.Count(d => d.TradeId == t.Id)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new AgentExperienceView(roles, trades, ComputedAt(rows));
    }

    public async Task<ShadowOutcomeView?> GetShadowOutcomeAsync(Guid userId, Guid evaluationId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shadow = await db.ShadowOutcomes.AsNoTracking()
            .Where(s => s.Decision.EvaluationId == evaluationId && s.Decision.Evaluation.Agent.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (shadow is null)
            return null;
        var detail = ShadowDetail.Parse(shadow.Detail);
        return new ShadowOutcomeView(shadow.Result, detail?.Reason ?? "", detail?.Resolution, shadow.RMultiple, detail?.CandlesSeen ?? 0,
            detail?.EntryTimeoutCandles ?? 0, detail?.HorizonCandles ?? 0, detail?.FilledAt, shadow.ResolvedAt);
    }

    /// <summary>Sums rows over the open filter dimensions; the loss denominator counts each timeframe and market once, as every row of one owner carries the same figure.</summary>
    internal static ExperienceMetrics Aggregate(IEnumerable<ExperienceStat> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var total = new ExperienceMetrics();
        var seen = new HashSet<(CandleInterval, MarketType)>();
        var loss = 0m;
        foreach (var row in rows)
        {
            total.Add(row.Metrics);
            if (seen.Add((row.Timeframe, row.MarketType)))
                loss += row.Metrics.ScopeGrossLossR;
        }

        total.ScopeGrossLossR = loss;
        return total.Derive();
    }

    /// <summary>Best expectancy first, then the larger sample, then the better average R, then the busier row (FR-EXP-07).</summary>
    internal static IReadOnlyList<LeaderboardRow> Rank(IEnumerable<ExperienceStat> rows) =>
        rows.GroupBy(s => (s.Provider, s.Model))
            .Select(g => new LeaderboardRow(g.Key.Provider, g.Key.Model, Aggregate(g)))
            .OrderByDescending(r => r.Metrics.Expectancy ?? decimal.MinValue)
            .ThenByDescending(r => r.Metrics.Trades)
            .ThenByDescending(r => r.Metrics.AverageR ?? decimal.MinValue)
            .ThenByDescending(r => r.Metrics.Decisions)
            .ThenBy(r => r.Provider)
            .ThenBy(r => r.Model, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<ModelRoleRow> ByModelAndRole(IEnumerable<ExperienceStat> rows) =>
        rows.GroupBy(s => (s.Model, s.Role))
            .Select(g => new ModelRoleRow(g.Key.Model, g.Key.Role, Aggregate(g)))
            .OrderBy(r => r.Model, StringComparer.Ordinal)
            .ThenBy(r => r.Role)
            .ToList();

    private static DateTimeOffset? ComputedAt(IEnumerable<ExperienceStat> rows) => rows.Select(s => (DateTimeOffset?)s.ComputedAt).Max();
}
