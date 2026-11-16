using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Reads the agents whose cycles run, of every user, for what starts a cycle (FR-CYC-1). It
/// changes nothing. See docs/agents.md.
/// </summary>
internal sealed class RunningAgents(IDbContextFactory<EcnanibDbContext> contexts) : IRunningAgents
{
    // The states in which the cycles of an agent run. AgentStates says which, so that the table
    // of the states stays in one place.
    private static readonly AgentState[] States = [.. Enum.GetValues<AgentState>().Where(AgentStates.RunsCycles)];

    public async Task<IReadOnlyList<RunningAgent>> OfTimeframesAsync(
        IReadOnlyCollection<Timeframe> timeframes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeframes);

        if (timeframes.Count == 0)
        {
            return [];
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Across users: a job has no user who is signed in, and takes the owner from the row.
        var agents = await context.Set<Agent>()
            .AsNoTracking()
            .Where(agent => States.Contains(agent.State) && timeframes.Contains(agent.Timeframe))
            .OrderBy(agent => agent.Id)
            .ToListAsync(cancellationToken);

        return await WithRolesAsync(context, agents, cancellationToken);
    }

    public async Task<RunningAgent?> FindAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var agents = await context.Set<Agent>()
            .AsNoTracking()
            .Where(agent => agent.Id == agentId && States.Contains(agent.State))
            .ToListAsync(cancellationToken);

        return (await WithRolesAsync(context, agents, cancellationToken)).SingleOrDefault();
    }

    /// <summary>
    /// Gives agents the roles of their teams. The roles are asked for by the ids of the agents
    /// that were read, and not by their state again: an agent that stops running between the two
    /// reads keeps its team here, and is found not to run when its turn comes.
    /// </summary>
    private static async Task<IReadOnlyList<RunningAgent>> WithRolesAsync(
        EcnanibDbContext context, List<Agent> agents, CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
        {
            return [];
        }

        var ids = agents.Select(agent => agent.Id).ToArray();

        var rows = await (
            from role in context.Set<AgentRole>().AsNoTracking()
            where ids.Contains(role.AgentId)
            join model in context.Set<AiModel>().AsNoTracking() on role.ModelId equals model.Id
            select new { role.AgentId, role.Role, role.ModelId, model.Provider, model.Identifier, model.DisplayName })
            .ToListAsync(cancellationToken);

        var roles = rows
            .OrderBy(row => row.Role)
            .ToLookup(row => row.AgentId, row => new AgentRoleView(row.Role, row.ModelId, row.Provider, row.Identifier, row.DisplayName));

        return [.. agents.Select(agent => new RunningAgent(agent, [.. roles[agent.Id]]))];
    }
}
