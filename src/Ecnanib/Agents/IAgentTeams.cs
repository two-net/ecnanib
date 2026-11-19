using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// An agent of a user with the team that it would run with, for what runs a test cycle of it
/// (FR-CYC-12): the agent as it is stored, in whatever state, the roles of its team that have a
/// model, and what about the team keeps it from working. See "A test cycle" in docs/cycles.md,
/// and docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// It is asked for the user who is signed in, by id: the Decision Engine asks it, which may not
/// use Accounts, and is handed that user by whatever serves the request. An agent of anyone else
/// is not found (NFR-SEC-3). See docs/ownership.md.
/// </para>
/// <para>
/// It only reads, and asks no provider. <see cref="IRunningAgents"/> finds the agents that run,
/// of every user, for the cycles that a candle close starts: a test cycle is run for an agent in
/// any state, a draft among them, and for its own user alone.
/// </para>
/// </remarks>
public interface IAgentTeams
{
    /// <summary>One agent of a user as it is stored now, with its team.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>The agent with its team, or null when that user has no agent of that id.</returns>
    Task<AgentTeam?> FindAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>An agent with its team, as both were stored when they were read.</summary>
/// <param name="Agent">The agent, with its settings as they were then, in whatever state it is.</param>
/// <param name="Roles">
/// The roles of its team that have a model, in the order of the roles, each with the provider
/// and the model that its user gave it.
/// </param>
/// <param name="Blocks">
/// What about the team keeps it from working, role by role, in the order of the roles: a role
/// that has no model, and a role whose model its user cannot select now. It is what keeps an
/// agent from being started (FR-TEAM-1). Empty when the team is whole and can work.
/// </param>
public sealed record AgentTeam(Agent Agent, IReadOnlyList<AgentRoleView> Roles, IReadOnlyList<RoleBlock> Blocks);

/// <summary>Reads an agent of a user with its team. It changes nothing.</summary>
internal sealed class AgentTeams(IDbContextFactory<EcnanibDbContext> contexts, AgentRoles roles) : IAgentTeams
{
    public async Task<AgentTeam?> FindAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: an agent of another user is not found, like one that does not exist.
        var agent = await context.Set<Agent>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(stored => stored.Id == agentId, cancellationToken);

        if (agent is null)
        {
            return null;
        }

        // What a start asks of the team, without the lock that a start takes: nothing here has
        // to hold until the agent runs. A key that goes while a test cycle runs is one that a
        // role of that cycle is not called with, which its record says.
        return new AgentTeam(
            agent,
            await AgentRoles.OfAgentAsync(context, userId, agentId, cancellationToken),
            await roles.BlocksAsync(context, userId, agentId, cancellationToken));
    }
}
