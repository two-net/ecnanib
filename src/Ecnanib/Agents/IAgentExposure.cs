using Ecnanib.Platform.Persistence;

namespace Ecnanib.Agents;

/// <summary>
/// What Agents asks about what an agent holds on the exchange, and asks to be done with it:
/// whether a stopped agent has an open position or orders, before its market, symbol, or
/// timeframe is changed and before it is deleted (FR-AGT-8); whether an agent has an open
/// position, which its user is asked about when they stop it (FR-AGT-11) and can close at any
/// time (FR-AGT-12); to close that position at market; and what in the account of its user at
/// the exchange stands in the way of an agent that is to run (FR-BIN-F2). Agents may not use the
/// Decision Engine, places no order, and is handed no key of an exchange, so it declares this
/// interface, and whatever keeps the positions and the orders implements and registers it. See
/// "When a lower component must trigger a higher one" in docs/architecture.md.
/// </summary>
/// <remarks>
/// <para>
/// The Decision Engine keeps the positions and the orders, and implements it: see
/// docs/orders.md. Where nothing answers, no agent holds any: nothing stands in the way, no user
/// is asked what to do with a position, none is closed, and no account is asked about.
/// </para>
/// <para>
/// A question is asked with the context of whoever asks, inside its transaction where it has
/// one. An implementation asks for the rows of the agent it is given, and commits nothing.
/// </para>
/// </remarks>
public interface IAgentExposure
{
    /// <summary>Whether an agent has an open position or open orders.</summary>
    /// <param name="context">The context of whoever asks.</param>
    /// <param name="agentId">The agent that is asked about.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);

    /// <summary>Whether an agent has an open position.</summary>
    /// <param name="context">The context of whoever asks.</param>
    /// <param name="agentId">The agent that is asked about.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<bool> HasOpenPositionAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the open position of an agent at market, and takes its protective orders off the
    /// exchange. The user of the agent asked for it, and Agents has recorded that: it is asked
    /// once, after the transaction that recorded it, and never inside one, because it waits for
    /// the exchange. It is asked in any state of the agent, so that risk can always be reduced.
    /// </summary>
    /// <remarks>
    /// An implementation answers with an outcome, and not with an error, when the exchange
    /// refuses or gives no answer. What it must not lose once it has taken the request is its
    /// own to keep.
    /// </remarks>
    /// <param name="agentId">The agent whose position is closed.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<PositionCloseOutcome> ClosePositionAtMarketAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// What in the account of an agent's user at the exchange stands in the way of the agent,
    /// as the exchange says it now (FR-BIN-F2). On the Futures market an agent trades its symbol
    /// in one-way mode, with isolated margin: an account in a mode that conflicts with that, or
    /// one that holds a position in the symbol which the agent did not open, is one that the
    /// agent does not start in. The service changes no setting of the whole account. It is
    /// asked before the transaction that starts the agent, and never inside it, because it
    /// waits for the exchange.
    /// </summary>
    /// <param name="agent">The agent that is to run.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>
    /// What stands in the way, <see cref="AccountConflict.NotKnown"/> when the exchange did not
    /// say, or null when nothing does. For an agent of the Spot market nothing does.
    /// </returns>
    Task<AccountConflict?> AccountConflictAsync(Agent agent, CancellationToken cancellationToken);
}

/// <summary>
/// What in the account of a user at the exchange stands in the way of an agent of the Futures
/// market, which trades one symbol in one-way mode with isolated margin (FR-BIN-F2). Each but
/// the last is for the user to change at the exchange: the service does not change it.
/// </summary>
public enum AccountConflict
{
    /// <summary>The account keeps a long and a short position of a symbol apart: hedge mode. An agent trades in one-way mode.</summary>
    HedgeMode,

    /// <summary>The account margins its positions with more than one asset, in which the exchange takes no isolated margin.</summary>
    MultiAssetsMode,

    /// <summary>The account holds a position in the symbol of the agent that the agent did not open.</summary>
    PositionInSymbol,

    /// <summary>The exchange did not say how the account stands. An account that could not be asked about is not one that nothing stands in the way of.</summary>
    NotKnown,
}

/// <summary>How closing the position of an agent at market ended.</summary>
public enum PositionCloseOutcome
{
    /// <summary>The position is closed, or the order that closes it is with the exchange.</summary>
    Closed,

    /// <summary>The agent has no open position.</summary>
    NothingOpen,

    /// <summary>The position could not be closed: the exchange refused, or gave no answer. It is open as it was, with its protective orders.</summary>
    Failed,
}

/// <summary>Asks everything that keeps positions and orders. Where nothing does, no agent holds any.</summary>
internal static class AgentExposures
{
    /// <summary>Whether an agent has an open position or open orders.</summary>
    public static async Task<bool> HoldPositionOrOrdersAsync(
        this IEnumerable<IAgentExposure> exposures, EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken)
    {
        foreach (var exposure in exposures)
        {
            if (await exposure.HasOpenPositionOrOrdersAsync(context, agentId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an agent has an open position.</summary>
    public static async Task<bool> HoldPositionAsync(
        this IEnumerable<IAgentExposure> exposures, EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken)
    {
        foreach (var exposure in exposures)
        {
            if (await exposure.HasOpenPositionAsync(context, agentId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What in the account of an agent's user at the exchange stands in the way of the agent:
    /// what the first that says something says. Where nothing answers, nothing stands in the way.
    /// </summary>
    public static async Task<AccountConflict?> AccountConflictAsync(
        this IEnumerable<IAgentExposure> exposures, Agent agent, CancellationToken cancellationToken)
    {
        foreach (var exposure in exposures)
        {
            if (await exposure.AccountConflictAsync(agent, cancellationToken) is { } conflict)
            {
                return conflict;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the mode, market, symbol, and timeframe of an agent can be changed, and the agent
    /// deleted (FR-AGT-8). What an agent holds on the exchange is asked only of a stopped one:
    /// a draft never held anything, and an active agent is refused whatever it holds.
    /// </summary>
    public static async Task<bool> StructureCanChangeAsync(
        this IEnumerable<IAgentExposure> exposures, EcnanibDbContext context, Agent agent, CancellationToken cancellationToken)
    {
        var holds = agent.State == AgentState.Stopped
            && await exposures.HoldPositionOrOrdersAsync(context, agent.Id, cancellationToken);

        return AgentStates.StructureCanChange(agent.State, holds);
    }

    /// <summary>
    /// Closes the open position of an agent at market. It failed when anything that keeps
    /// positions says so, and nothing was open when nothing closed one.
    /// </summary>
    public static async Task<PositionCloseOutcome> ClosePositionAtMarketAsync(
        this IEnumerable<IAgentExposure> exposures, Guid agentId, CancellationToken cancellationToken)
    {
        var outcome = PositionCloseOutcome.NothingOpen;

        foreach (var exposure in exposures)
        {
            switch (await exposure.ClosePositionAtMarketAsync(agentId, cancellationToken))
            {
                case PositionCloseOutcome.Failed:
                    return PositionCloseOutcome.Failed;

                case PositionCloseOutcome.Closed:
                    outcome = PositionCloseOutcome.Closed;
                    break;
            }
        }

        return outcome;
    }
}
