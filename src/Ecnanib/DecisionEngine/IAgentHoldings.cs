using Ecnanib.Agents;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What an agent holds as its cycle starts: its equity, its open position, and its open orders,
/// which the data package of the cycle contains (FR-CYC-2). Whatever keeps the positions and the
/// orders implements this and registers it. See "What an agent holds" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// What keeps the positions and the orders answers here, from its rows: see docs/orders.md. The
/// equity of an agent is the capital that its user allocated to it, plus what its positions
/// made and lost, with what its open position would make now. Where nothing answers, an agent
/// holds nothing.
/// </para>
/// <para>
/// It is asked for one agent, as its data package is built, and again as the Risk Guard checks
/// its plan, and only reads. What it answers is what a prompt says of the agent, so it holds no
/// id and no name (FR-PRM-9). What closes the position of an agent and halts it when a loss
/// limit is reached does not ask here: see <see cref="ILossLimits"/>.
/// </para>
/// </remarks>
public interface IAgentHoldings
{
    /// <summary>The equity, the open position, and the open orders of an agent, now.</summary>
    /// <param name="agent">The agent whose cycle is building its data package.</param>
    /// <param name="cancellationToken">Canceled when the cycle is stopped.</param>
    Task<AgentHoldings> OfAsync(Agent agent, CancellationToken cancellationToken);
}

/// <summary>What an agent holds.</summary>
/// <param name="Equity">
/// The capital its user allocated to it, plus its net profit or loss to date, in the asset that
/// its symbol is priced in. It includes unrealized profit and loss.
/// </param>
/// <param name="Position">Its open position, or null when it has none. An agent has one at most.</param>
/// <param name="Orders">
/// Its orders that rest on the exchange: an entry that has not filled, and the stop-loss and the
/// take-profit of its position. Empty when it has none.
/// </param>
/// <param name="EquityAtDayStart">
/// Its equity as the UTC day began, which its loss of the day is counted from: the Risk Guard
/// holds a plan that opens a position to the daily loss limit (FR-RSK-4).
/// </param>
/// <param name="PeakEquity">
/// The highest that its equity has been, which its drawdown is counted from: the Risk Guard
/// holds a plan that opens a position to the drawdown limit (FR-RSK-4).
/// </param>
/// <param name="Valued">
/// Whether the equity is known. It is not where the exchange did not say the price that the open
/// position is valued at: <paramref name="Equity"/> is then without what that position would
/// make, and no plan opens a position on it, for the loss limits are not known to hold.
/// </param>
public sealed record AgentHoldings(
    decimal Equity,
    PositionContext? Position,
    IReadOnlyList<OpenOrder> Orders,
    decimal EquityAtDayStart,
    decimal PeakEquity,
    bool Valued = true)
{
    /// <summary>
    /// What an agent holds while nothing keeps positions and orders: nothing, with the capital
    /// that its user allocated to it as its equity, for it has made no profit and no loss. So
    /// it has lost nothing in the day, and is at its peak.
    /// </summary>
    public static AgentHoldings NothingYet(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentHoldings(
            agent.AllocatedCapital,
            Position: null,
            Orders: [],
            EquityAtDayStart: agent.AllocatedCapital,
            PeakEquity: agent.AllocatedCapital);
    }
}

/// <summary>An order of an agent that rests on the exchange, as a data package says it.</summary>
/// <param name="Kind">What the order is for.</param>
/// <param name="Side">Whether it buys or sells.</param>
/// <param name="Quantity">How much of the asset that is traded it is for.</param>
/// <param name="Price">The price of a limit entry, or the price that a stop-loss or a take-profit is triggered at.</param>
public sealed record OpenOrder(OpenOrderKind Kind, OrderSide Side, decimal Quantity, decimal Price);

/// <summary>What an open order is for.</summary>
public enum OpenOrderKind
{
    /// <summary>It opens a position, and has not filled yet.</summary>
    Entry,

    /// <summary>It closes the position at a loss, when the price reaches it.</summary>
    StopLoss,

    /// <summary>It closes the position at a profit, when the price reaches it.</summary>
    TakeProfit,
}

/// <summary>The side of an order.</summary>
public enum OrderSide
{
    /// <summary>It buys the asset that is traded.</summary>
    Buy,

    /// <summary>It sells the asset that is traded.</summary>
    Sell,
}
