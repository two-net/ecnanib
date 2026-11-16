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
/// Only the data package is built yet: a cycle starts, builds it, and makes no decision. What is
/// built behind this takes the cycle from there, and changes nothing of when a cycle starts.
/// </para>
/// <para>
/// A cycle is run for an agent that ran when its turn came. An agent is paused or stopped by its
/// user at any moment, also while its cycle runs, so what places an entry asks again then.
/// </para>
/// </remarks>
public interface IDecisionCycle
{
    /// <summary>Runs one cycle of an agent, for the close of one candle.</summary>
    /// <param name="cycle">What the cycle is for.</param>
    /// <param name="cancellationToken">
    /// Canceled when the application stops, when it stops being the instance that runs the
    /// background work, and half a minute before the next close, by which the cycles of a close
    /// are over. The cycle then ends soon, by throwing <see cref="OperationCanceledException"/>,
    /// and leaves no entry without its protection (NFR-REL-5).
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
public sealed record CycleStart(
    Guid CycleId,
    Agent Agent,
    IReadOnlyList<AgentRoleView> Roles,
    DateTimeOffset ClosedAt,
    ExchangeCandles Candles);

/// <summary>
/// The inside of a cycle while the calls of the team are not built: it builds the data package
/// of the cycle (FR-CYC-2), which is as far as a cycle goes yet, and ends with no decision and no
/// order.
/// </summary>
/// <remarks>
/// A package that cannot be built leaves this as an error, as every failure of a cycle does: the
/// cycle ends with no decision, and the others go on.
/// </remarks>
internal sealed class NoDecisionYet(IDataPackages packages) : IDecisionCycle
{
    // Nobody reads the package yet. What calls the team hands it to each role, as the parts of
    // its prompt.
    public async Task RunAsync(CycleStart cycle, CancellationToken cancellationToken) =>
        _ = await packages.BuildAsync(cycle, cancellationToken);
}
