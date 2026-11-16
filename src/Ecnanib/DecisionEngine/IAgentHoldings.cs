using Ecnanib.Agents;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What an agent holds as its cycle starts: its equity, its open position, and its open orders,
/// which the data package of the cycle contains (FR-CYC-2). Whatever keeps the positions and the
/// orders implements this and registers it. See "What an agent holds" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// Nothing implements it yet: positions and orders are not built, and no result of an agent is
/// stored. Until then an agent holds nothing, and its equity is the capital that its user
/// allocated to it. A test fails as soon as a table of positions or of orders is in the model and
/// nothing answers here.
/// </para>
/// <para>
/// It is asked for one agent, as its data package is built, and only reads. What it answers is
/// what a prompt says of the agent, so it holds no id and no name (FR-PRM-9).
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
/// its symbol is priced in.
/// </param>
/// <param name="Position">Its open position, or null when it has none. An agent has one at most.</param>
/// <param name="Orders">
/// Its orders that rest on the exchange: an entry that has not filled, and the stop-loss and the
/// take-profit of its position. Empty when it has none.
/// </param>
public sealed record AgentHoldings(decimal Equity, PositionContext? Position, IReadOnlyList<OpenOrder> Orders)
{
    /// <summary>
    /// What an agent holds while nothing keeps positions and orders: nothing, with the capital
    /// that its user allocated to it as its equity, for it has made no profit and no loss.
    /// </summary>
    public static AgentHoldings NothingYet(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentHoldings(agent.AllocatedCapital, Position: null, Orders: []);
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
