using Ecnanib.Agents;
using Ecnanib.RiskGuard;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The orders of a cycle (SRS 3.2, step 7): what places the entry and the protective order of a
/// plan that the Risk Guard accepted, or does nothing, and what settles the orders of an agent
/// with the exchange. See docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An order is never sent twice</b> (FR-BIN-6). Every order carries an identifier of its own,
/// which is stored before the order is sent. An order of which the exchange did not say what
/// became of it is asked about by that identifier before anything else is done with it.
/// </para>
/// <para>
/// <b>Every open position has a stop-loss that rests on the exchange</b> (FR-RSK-7). When the
/// stop-loss cannot be placed after an entry is executed, the position is closed at market at
/// once, and its user is told.
/// </para>
/// <para>
/// It is public so that a test can put a stand-in behind it, for cycles of agents that need no
/// rows: the tests see no internal type.
/// </para>
/// </remarks>
public interface IPlanOrders
{
    /// <summary>
    /// Places the orders of a plan that the Risk Guard accepted: an entry at the market with
    /// its stop-loss, the close of the open position, both for a plan that reverses, or the
    /// stop-loss at its new price. What it places is made from the result, and from nothing else
    /// (NFR-SEC-6): the quantity is the one that the Risk Guard computed (FR-RSK-5).
    /// </summary>
    /// <param name="cycle">The cycle whose plan it is.</param>
    /// <param name="accepted">What the Risk Guard accepted.</param>
    /// <param name="cancellationToken">
    /// Canceled when the cycle is stopped. Nothing is begun after that. An entry that was sent
    /// by then is still protected: its stop-loss is placed once, and the rest is left to what
    /// settles the orders (NFR-REL-5).
    /// </param>
    /// <exception cref="ArgumentException">The result is of a plan that the Risk Guard rejected.</exception>
    Task PlaceAsync(CycleStart cycle, GuardResult accepted, CancellationToken cancellationToken);

    /// <summary>
    /// Settles the orders of an agent with the exchange, as far as the exchange says: an order
    /// of unknown outcome is asked about, the stop-loss that rests is asked about, an open
    /// position without one is protected or closed at market, and the executions of the orders
    /// that are over are stored. It sends no order that the exchange may have already.
    /// </summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Canceled when what asked is stopped. Nothing is begun after that.</param>
    Task SettleAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the open position of an agent at market, for its user (FR-AGT-11, FR-AGT-12): the
    /// stop-loss is taken off the exchange, and the position is closed. When it cannot be
    /// closed, the stop-loss is placed again.
    /// </summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Canceled when what asked is stopped.</param>
    /// <returns>How it ended. An exchange that refuses or gives no answer is an outcome: this does not fail for it.</returns>
    Task<PositionCloseOutcome> CloseAtMarketAsync(Guid agentId, CancellationToken cancellationToken);
}
