using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>Deletes an agent's decision cycles with it. Their roles, AI calls and evaluations go through their foreign keys.</summary>
internal sealed class AgentCycleHistory : IAgentHistory
{
    public Task DeleteAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken) =>
        context.Set<DecisionCycleRow>()
            .Where(cycle => cycle.UserId == userId && cycle.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
}
