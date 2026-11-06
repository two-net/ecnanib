using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>The scheduler's view of agents: every user's Active ones on a timeframe. See Scheduler in docs/architecture.md.</summary>
internal sealed class ActiveAgents(IDbContextFactory<EcnanibDbContext> contextFactory) : IActiveAgents
{
    public async Task<IReadOnlyList<ActiveAgent>> ListAsync(Timeframe timeframe, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<Agent>()
            .AsNoTracking()
            .Where(agent => agent.Status == AgentStatus.Active && agent.Timeframe == timeframe)
            .Select(agent => new ActiveAgent(agent.Id, agent.UserId))
            .ToListAsync(cancellationToken);
    }
}
