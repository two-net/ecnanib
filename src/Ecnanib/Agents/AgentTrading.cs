using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// What whatever places the orders of an agent asks of Agents (FR-RSK-7). See
/// <see cref="IAgentTrading"/> and docs/agents.md.
/// </summary>
internal sealed class AgentTrading(
    IDbContextFactory<EcnanibDbContext> contexts, TimeProvider clock, ILogger<AgentTrading> logger) : IAgentTrading
{
    public async Task<AgentState?> StateUnderLockAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The state of an agent is asked under the lock inside the transaction that writes its order, and this context holds none.");
        }

        if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
        {
            // The account was deleted since the cycle started.
            return null;
        }

        // Asked for by owner, under the lock: the state as it is now, whatever the cycle was handed.
        var states = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(agent => agent.Id == agentId)
            .Select(agent => agent.State)
            .ToListAsync(cancellationToken);

        return states.Count == 1 ? states[0] : null;
    }

    public async Task<bool> RequireAttentionAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (!await StoredAgents.LockAccountAsync(context, userId, cancellationToken))
        {
            return false;
        }

        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(agent => agent.Id == agentId)
            .Select(agent => new { agent.State, agent.Mode })
            .SingleOrDefaultAsync(cancellationToken);

        // Only an agent that runs: one that its user paused or stopped stays as its user left it.
        if (agent is null || !AgentStates.RunsCycles(agent.State))
        {
            return false;
        }

        await AgentStateChanges.MoveAsync(
            context,
            userId,
            agentId,
            agent.Mode,
            AgentState.AttentionRequired,
            AgentStateCause.PositionUnprotected,
            clock.GetUtcNow(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning(
            "Agent {AgentId} of user {UserId} requires attention: {AgentStateCause}", agentId, userId, AgentStateCause.PositionUnprotected);

        return true;
    }
}
