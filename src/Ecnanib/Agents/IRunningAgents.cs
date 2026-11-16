using Ecnanib.Shared;

namespace Ecnanib.Agents;

/// <summary>
/// The agents whose cycles run, for what starts a cycle when a candle closes (FR-CYC-1): only an
/// agent in the Running state gets one (SRS 3.3). See docs/agents.md and docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// It is asked by background work, where nobody is signed in, so it reads the agents of every
/// user. Each agent says whose it is, and what is done for it is done for that user. No page and
/// no endpoint asks it: a request reads the agents of its own user, through
/// <see cref="IStoredAgents"/>. See "What is not asked for by owner" in docs/ownership.md.
/// </para>
/// <para>
/// It only reads. Which states run cycles is <see cref="AgentStates.RunsCycles"/>, and nothing
/// here names a state.
/// </para>
/// </remarks>
public interface IRunningAgents
{
    /// <summary>
    /// The agents of all users that run cycles now and work on one of the timeframes, in the
    /// order of their ids, each with the roles of its team that have a model.
    /// </summary>
    /// <param name="timeframes">The timeframes of which a candle closed. None gives no agent.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    Task<IReadOnlyList<RunningAgent>> OfTimeframesAsync(
        IReadOnlyCollection<Timeframe> timeframes, CancellationToken cancellationToken);

    /// <summary>
    /// One agent as it is stored now, with its roles, when it runs cycles now. It is asked as the
    /// turn of a cycle comes: an agent that was paused, stopped, or deleted while its cycle
    /// waited, or that requires attention by then, is not found, and gets no cycle.
    /// </summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>The agent, or null when no agent of that id runs cycles now.</returns>
    Task<RunningAgent?> FindAsync(Guid agentId, CancellationToken cancellationToken);
}

/// <summary>An agent that runs cycles, as it was stored when it was read.</summary>
/// <param name="Agent">The agent, with its settings as they were then.</param>
/// <param name="Roles">
/// The roles of its team that have a model, in the order of the roles, each with the provider
/// and the model that its user gave it. An agent runs with its whole team, so there are four,
/// unless the agent was started before a role could be given a model.
/// </param>
public sealed record RunningAgent(Agent Agent, IReadOnlyList<AgentRoleView> Roles);
