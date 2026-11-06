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
}
