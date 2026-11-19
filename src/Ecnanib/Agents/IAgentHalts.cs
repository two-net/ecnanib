using Ecnanib.Platform.Persistence;

namespace Ecnanib.Agents;

/// <summary>
/// What whatever watches the loss limits of an agent asks of Agents (FR-RSK-8): that an agent
/// which reached one be halted, and that an agent which its daily loss limit halted run again
/// when the next UTC day begins. Agents works out no result and closes no position: what keeps
/// the positions does both. See "Halting an agent" in docs/risk-guard.md, and docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// It is asked by background work, where nobody is signed in: it takes the user and the agent by
/// their ids, which are those of the agent's own row and never something that came in from
/// outside. See docs/ownership.md.
/// </para>
/// <para>
/// <b>What it does is the service's doing, and no action of a user.</b> The change of the state
/// records it, with its cause and its time (FR-AGT-10), and the audit trail does not.
/// </para>
/// </remarks>
public interface IAgentHalts
{
    /// <summary>
    /// One agent as it is stored now, in whatever state it is, for what watches its loss limits
    /// when its account is reconciled: a position is reconciled whether its agent runs, is
    /// paused, waits for its user, or was stopped with its position kept. It only reads.
    /// </summary>
    /// <param name="agentId">The agent, by the id that a position or an order of it says.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>The agent, or null when there is none of that id any more.</returns>
    Task<Agent?> FindAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves an agent that runs to Halted, because it reached a loss limit, inside the
    /// transaction of whoever asks, which also writes what the limit is counted from. It locks
    /// the row of the account for the rest of that transaction, as the controls of its user do.
    /// An agent in any other state stays as it is: one that its user paused or stopped, or that
    /// waits for its user, keeps its position with its protective orders (SRS 3.3). Whoever asks
    /// commits, and has the position of the agent closed after that.
    /// </summary>
    /// <param name="context">The context of whoever asks, which holds a transaction.</param>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cause">
    /// Which limit: <see cref="AgentStateCause.DailyLossLimitReached"/> or
    /// <see cref="AgentStateCause.DrawdownLimitReached"/>.
    /// </param>
    /// <param name="at">
    /// The moment that the limit was found reached at, which is since when the agent is halted.
    /// A daily loss limit halts until the UTC day after this moment, so it is the moment that
    /// the loss of the day was worked out for, and not a later one.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the agent was moved.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The cause is no loss limit.</exception>
    /// <exception cref="InvalidOperationException">The context holds no transaction.</exception>
    Task<bool> HaltAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, AgentStateCause cause, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes every agent, of all users, that its daily loss limit halted on an earlier UTC day
    /// than this one: the agent is halted until the next UTC day, and runs again from then on
    /// (FR-RSK-8). It is asked as a candle closes, before the agents that run are read, so that
    /// such an agent has its cycle of the first close of the day.
    /// </summary>
    /// <remarks>
    /// An agent is resumed when what a resume of its user asks without the exchange allows it:
    /// a valid Binance key that may trade on its market (FR-KEY-5), and a team that can work
    /// (FR-TEAM-1). Otherwise it stays halted, is asked about again with the next close, and
    /// its user can resume it. Its user is not asked once more for an agent in Live: the halt
    /// ends as the requirement says it does. And the account of its user at the exchange is not
    /// asked about: it is asked about before every entry of an agent that runs (FR-BIN-F2). An
    /// agent that its drawdown limit halted is not resumed here: its user resumes it.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the work. An agent that was resumed by then stays so.</param>
    /// <returns>How many agents were resumed.</returns>
    Task<int> EndDailyHaltsAsync(CancellationToken cancellationToken);
}
