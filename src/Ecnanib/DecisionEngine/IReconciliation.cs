using Ecnanib.Agents;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// What reconciles the records of an agent with the exchange, as far as the cycles ask it: after
/// a restart the system reconciles first, and a cycle starts only then (FR-CYC-8, NFR-REL-3).
/// Whatever reconciles implements this and registers it. See docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// Nothing implements it yet: reconciliation is not built, and no position or order is stored
/// that could differ from the exchange. Until then every agent passes. A test fails as soon as a
/// table of positions or of orders is in the model and nothing answers here.
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
    /// Whether the records of an agent have been reconciled with the exchange since this instance
    /// took up the background work: at the start of the application, and again after the lock of
    /// the background work was lost and taken back.
    /// </summary>
    /// <param name="agent">The agent whose cycle is about to start.</param>
    /// <param name="cancellationToken">Canceled when the cycles of the close are stopped.</param>
    Task<bool> HasReconciledAsync(RunningAgent agent, CancellationToken cancellationToken);
}
