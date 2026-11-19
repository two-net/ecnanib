using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What a cycle does once it has started: the data package, the calls of the team, the Risk
/// Guard, and the orders (SRS 3.2, steps 2 to 8). What starts a cycle when a candle closes, and
/// lets it wait in the queue for its turn, calls this when the turn comes (FR-CYC-1, FR-CYC-11).
/// See docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// <c>TeamCycle</c> builds the package, runs the four roles, has the Risk Guard check what goes
/// on of the plan, and hands what the Risk Guard accepted to what places the orders. See
/// docs/team.md, docs/risk-guard.md, and docs/orders.md.
/// </para>
/// <para>
/// A cycle is run for an agent that ran when its turn came. An agent is paused or stopped by its
/// user at any moment, also while its cycle runs, so what places an entry asks again then.
/// </para>
/// <para>
/// A test cycle, which its user runs for an agent in any state, is handed over as a cycle is,
/// with <see cref="CycleStart.TestedAt"/>: it is recorded, and places no order (FR-CYC-12).
/// </para>
/// </remarks>
public interface IDecisionCycle
{
    /// <summary>Runs one cycle of an agent, for the close of one candle.</summary>
    /// <param name="cycle">What the cycle is for.</param>
    /// <param name="cancellationToken">
    /// Canceled when the application stops, when it stops being the instance that runs the
    /// background work, and 45 seconds before the next close, by which the cycles of a close
    /// are over. The cycle then ends soon, by throwing <see cref="OperationCanceledException"/>,
    /// and leaves no entry without its protection (NFR-REL-5). For a test cycle it is canceled
    /// when the application stops, and when the cycle runs longer than a test cycle may.
    /// </param>
    /// <remarks>
    /// An error that leaves this ends the cycle with no decision. It is logged once, with the
    /// agent and the cycle, and the other cycles go on.
    /// </remarks>
    Task RunAsync(CycleStart cycle, CancellationToken cancellationToken);
}

/// <summary>What a cycle is handed as it starts.</summary>
/// <param name="CycleId">
/// The id of the cycle, which everything logged for it carries, and which what stores a cycle
/// stores it under.
/// </param>
/// <param name="Agent">
/// The agent, with its settings as they were stored when the turn of the cycle came: a change
/// applies from the agent's next cycle (FR-AGT-8).
/// </param>
/// <param name="Roles">The roles of its team that have a model, in the order of the roles, as they were stored then.</param>
/// <param name="ClosedAt">The close of the candle that the cycle is for: a UTC boundary of the agent's timeframe.</param>
/// <param name="Candles">
/// The closed candles of the agent's symbol and timeframe, oldest first, as the exchange gave
/// them after the close: the newest of them closed at <paramref name="ClosedAt"/>, and the candle
/// that is in progress is not among them (FR-CYC-1). The agents that share a symbol and a
/// timeframe are handed the same.
/// </param>
/// <param name="TestedAt">
/// When its user ran the cycle, for a test cycle (FR-CYC-12): it places no order, settles
/// nothing with the exchange, and halts nothing. Null for a cycle that a candle close started.
/// See "A test cycle" in docs/cycles.md.
/// </param>
public sealed record CycleStart(
    Guid CycleId,
    Agent Agent,
    IReadOnlyList<AgentRoleView> Roles,
    DateTimeOffset ClosedAt,
    ExchangeCandles Candles,
    DateTimeOffset? TestedAt = null)
{
    /// <summary>Whether its user ran the cycle as a test, which places no order (FR-CYC-12).</summary>
    public bool IsTest => TestedAt is not null;

    /// <summary>
    /// What the stale limit of the decision counts from (FR-CYC-9): the close of the candle, and
    /// for a test cycle the moment that its user ran it. A test cycle is run at any time, hours
    /// after the close of its candle, and decides as a cycle does whose candle has just closed.
    /// </summary>
    public DateTimeOffset StaleFrom => TestedAt ?? ClosedAt;
}
