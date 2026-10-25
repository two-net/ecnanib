using Ecnanib.Agents.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Execution.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Services;

/// <summary>Sums the trades and the day's fills per agent in two grouped queries; the figures agree with the paper venue's equity and the daily loss limit.</summary>
internal sealed class AgentPerformance : IAgentPerformance
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IOptions<PaperOptions> _paper;
    private readonly TimeProvider _time;

    public AgentPerformance(IDbContextFactory<EcnanibDbContext> factory, IOptions<PaperOptions> paper, TimeProvider time)
    {
        _factory = factory;
        _paper = paper;
        _time = time;
    }

    public async Task<IReadOnlyDictionary<Guid, AgentPnl>> GetAsync(Guid userId, IReadOnlyCollection<Guid> agentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var ids = agentIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, AgentPnl>();

        var dayStart = PnlMath.UtcDayStart(_time.GetUtcNow());
        var nextDay = dayStart.AddDays(1);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.UserId == userId && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Mode })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var owned = agents.Select(a => a.Id).ToList();
        var trades = await db.Trades.AsNoTracking()
            .Where(t => owned.Contains(t.AgentId))
            .GroupBy(t => t.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Realized = g.Sum(t => t.RealizedPnl ?? 0m),
                PaperRealized = g.Sum(t => t.IsPaper ? (t.RealizedPnl ?? 0m) : 0m),
                Fees = g.Sum(t => t.Fees),
                Funding = g.Sum(t => t.Funding),
                Closed = g.Count(t => t.ClosedAt != null),
                Open = g.Count(t => t.ClosedAt == null),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // FR-EXE-09's day: exit fills carry their fee inside realized_pnl; the entry fees paid today are the other realized cost.
        var today = await db.Orders.AsNoTracking()
            .Where(o => owned.Contains(o.AgentId) && o.FilledAt != null && o.FilledAt >= dayStart && o.FilledAt < nextDay)
            .GroupBy(o => o.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Realized = g.Sum(o => o.RealizedPnl ?? 0m),
                EntryFees = g.Sum(o => o.Purpose == OrderPurpose.Entry ? (o.Fee ?? 0m) : 0m),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<Guid, AgentPnl>(agents.Count);
        foreach (var agent in agents)
        {
            var t = trades.FirstOrDefault(x => x.AgentId == agent.Id);
            var d = today.FirstOrDefault(x => x.AgentId == agent.Id);
            var paperEquity = agent.Mode == AgentMode.Paper ? _paper.Value.StartingBalance + (t?.PaperRealized ?? 0m) : (decimal?)null;
            result[agent.Id] = new AgentPnl(agent.Id, t?.Realized ?? 0m, (d?.Realized ?? 0m) - (d?.EntryFees ?? 0m), t?.Fees ?? 0m, t?.Funding ?? 0m, t?.Closed ?? 0, t?.Open ?? 0, paperEquity);
        }

        return result;
    }
}
