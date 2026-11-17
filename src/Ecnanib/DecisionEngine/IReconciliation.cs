using Ecnanib.Agents;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What reconciles the records of an agent with the exchange, as far as the cycles ask it: after
/// a restart the system reconciles first, and a cycle starts only then (FR-CYC-8, NFR-REL-3).
/// Whatever reconciles implements this and registers it. See docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// What settles the orders answers here: an agent that has an order of which it is not known
/// whether the exchange has it, or an open position without a stop-loss that rests, is not
/// reconciled, and gets no cycle until that is settled. So a restart places no order twice
/// (NFR-REL-3). See "Settling" in docs/orders.md. What compares everything that an account
/// holds with the records, at the start and on a schedule, is not built: it answers here as
/// well, when it is, and an agent passes when every one that answers says so. Where nothing
/// answers, every agent passes.
/// </para>
/// <para>
/// It is asked for one agent, as the turn of its cycle comes, and answers at once. An agent that
/// is not reconciled yet gets no cycle for that close, and no other agent waits for it: an
/// account that cannot be reconciled, because its key is refused, keeps nobody else from trading.
/// </para>
/// </remarks>
public interface IReconciliation
{
    /// <summary>
    /// Whether the records of an agent are reconciled with the exchange, as far as what answers
    /// knows: that nothing of the agent's orders is left to settle, and, of what reconciles the
    /// whole account, that it has done so since this instance took up the background work.
    /// </summary>
    /// <param name="agent">The agent whose cycle is about to start.</param>
    /// <param name="cancellationToken">Canceled when the cycles of the close are stopped.</param>
    Task<bool> HasReconciledAsync(RunningAgent agent, CancellationToken cancellationToken);
}
