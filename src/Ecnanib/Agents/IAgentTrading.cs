using Ecnanib.Platform.Persistence;

namespace Ecnanib.Agents;

/// <summary>
/// What whatever runs the cycles and places the orders of an agent asks of Agents: the state
/// that the agent is in as an order of it is written, under the lock that its user's controls
/// take, and that the agent wait for its user when its position is left without protection
/// (FR-RSK-7), when the account of its user at the exchange stands in the way of its entries
/// (FR-BIN-F2), or when an AI provider keeps refusing the calls with the key of its user
/// (NFR-REL-4). Agents places no order and calls no model. See docs/agents.md, docs/orders.md,
/// and docs/reliability.md.
/// </summary>
/// <remarks>
/// It is asked by background work, where nobody is signed in: it takes the user and the agent by
/// their ids, which are those of the agent's own row and never something that came in from
/// outside. See docs/ownership.md.
/// </remarks>
public interface IAgentTrading
{
    /// <summary>
    /// The state that an agent is in now, read under the lock of its account. It is asked inside
    /// the transaction that writes an order of the agent, and locks the row of the account for
    /// the rest of it, as stopping the agent does. So an entry and a stop of the agent's user
    /// cannot cross: either the entry is written first, and the stop finds it, or the agent is
    /// stopped first, and no entry is written (<see cref="AgentStates.TakesNewEntries"/>). And two
    /// that write an order of the same agent write one after the other.
    /// </summary>
    /// <param name="context">The context of whoever asks, which holds a transaction.</param>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>The state of the agent, or null when the account or the agent is not there.</returns>
    /// <exception cref="InvalidOperationException">The context holds no transaction.</exception>
    Task<AgentState?> StateUnderLockAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves an agent that runs to Attention required: because its position is open with no
    /// stop-loss on the exchange and could not be closed at market either, because a setting of
    /// its user's account at the exchange conflicts with how it trades, or because an AI
    /// provider keeps refusing the calls with the key of its user. An agent in any other state
    /// stays as it is: one that its user paused or stopped is not made to look as if it could be
    /// resumed for this.
    /// </summary>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cause">
    /// Why: <see cref="AgentStateCause.PositionUnprotected"/>,
    /// <see cref="AgentStateCause.ExchangeSettingConflict"/>, or
    /// <see cref="AgentStateCause.CallsFailRepeatedly"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the agent was moved.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The cause is none that what runs the cycles finds.</exception>
    Task<bool> RequireAttentionAsync(Guid userId, Guid agentId, AgentStateCause cause, CancellationToken cancellationToken);
}
