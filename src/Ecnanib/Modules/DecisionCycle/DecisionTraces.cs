using Ecnanib.Kernel;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>A user's decision traces. Every query filters on the user (NFR-SEC-04).</summary>
internal sealed class DecisionTraces(IDbContextFactory<EcnanibDbContext> contextFactory) : IDecisionTraces
{
    public async Task<CycleProgress?> FindLatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cycle = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Include(row => row.Roles)
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .OrderByDescending(row => row.CandleClose)
            .FirstOrDefaultAsync(cancellationToken);

        return cycle is null
            ? null
            : new CycleProgress(
                cycle.Id,
                cycle.UserId,
                cycle.AgentId,
                cycle.CandleClose,
                cycle.StartedAt,
                cycle.FinishedAt,
                cycle.Outcome,
                cycle.Failure,
                [
                    .. cycle.Roles
                        .OrderBy(role => role.Role)
                        .Select(role => new RoleProgress(role.Role, role.State, role.Provider, role.ModelId, role.UsedFallback, role.Summary)),
                ]);
    }

    public async Task<IReadOnlyList<CycleMarker>> ListCyclesAsync(Guid userId, Guid agentId, CycleRange range, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The agent and candle close index, newest first, so the cap keeps the cycles nearest now.
        var cycles = await context.Set<DecisionCycleRow>()
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.AgentId == agentId && row.Environment == range.Environment && row.Symbol == range.Symbol
                && row.Timeframe == range.Timeframe && row.CandleClose >= range.From && row.CandleClose <= range.To)
            .OrderByDescending(row => row.CandleClose)
            .Take(CycleRange.MaxCycles)
            .Select(row => new
            {
                row.Id,
                row.CandleClose,
                row.Environment,
                row.Outcome,
                row.Failure,
                Proposal = row.Roles.Where(role => role.Role == AiRole.Strategist).Select(role => role.Summary).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. cycles
                .OrderBy(cycle => cycle.CandleClose)
                .Select(cycle => new CycleMarker(cycle.Id, cycle.CandleClose, cycle.Environment, cycle.Outcome, cycle.Failure, DecisionCycles.ActionOf(cycle.Proposal))),
        ];
    }
}
