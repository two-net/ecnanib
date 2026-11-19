using System.Collections.Concurrent;
using Ecnanib.Agents;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Reconciles agents with the exchange, and remembers which of them it has reconciled since this
/// instance took up the background work (FR-BIN-7, NFR-REL-3). The job that reconciles on a
/// schedule and what follows the account event feed both reconcile through it, so that what
/// either found is known to what starts a cycle. See "Reconciling" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// What is remembered is in memory, for this process: an application that starts has reconciled
/// nothing, and that is what holds the first cycles back until the exchange was asked. An
/// instance that takes the background work up again, after it lost the lock, begins anew as
/// well: another instance may have placed orders meanwhile.
/// </para>
/// <para>
/// It places nothing itself, and reads no exchange: <see cref="IPlanOrders.ReconcileAsync"/>
/// does, for one agent, one after the other with everything else that is done for the agent.
/// </para>
/// </remarks>
internal sealed class AccountReconciliation(IPlanOrders orders, StoredOrders stored, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _reconciled = new();

    // Since when this instance does the background work, in ticks of UTC.
    private long _since = clock.GetUtcNow().UtcTicks;

    /// <summary>Since when this instance has done the background work: what an agent held before that is what a cycle waits for.</summary>
    public DateTimeOffset Since => new(Volatile.Read(ref _since), TimeSpan.Zero);

    /// <summary>
    /// Says that this instance takes up the background work now: at the start of the
    /// application, and again when the lock of the background work was lost and taken back. What
    /// was reconciled before does not count any more.
    /// </summary>
    public void TakenUp()
    {
        Volatile.Write(ref _since, clock.GetUtcNow().UtcTicks);
        _reconciled.Clear();
    }

    /// <summary>
    /// Reconciles one agent with the exchange, and remembers it when the agent is reconciled.
    /// </summary>
    /// <returns>Whether the agent is reconciled. An exchange that does not answer is no error: it is not, yet.</returns>
    public async Task<bool> ReconcileAsync(Guid agentId, CancellationToken cancellationToken)
    {
        // As of when the exchange began to be asked: what the agent comes to hold later is not in it.
        var asked = clock.GetUtcNow();

        if (!await orders.ReconcileAsync(agentId, cancellationToken))
        {
            return false;
        }

        _reconciled.AddOrUpdate(agentId, asked, (_, before) => asked > before ? asked : before);

        return true;
    }

    /// <summary>
    /// Whether an agent is reconciled with the exchange, as far as a cycle of it waits for that:
    /// it was reconciled since this instance took up the background work, or it holds nothing
    /// that was there before: no position that was open then, and no order that was not over.
    /// What it came to hold since, this instance placed, and saw placed.
    /// </summary>
    public async Task<bool> HasReconciledAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var since = Since;

        return (_reconciled.TryGetValue(agentId, out var at) && at >= since)
            || !await stored.HeldBeforeAsync(agentId, since, cancellationToken);
    }
}

/// <summary>
/// Says whether an agent was reconciled with the exchange since this instance took up the
/// background work, which a cycle of the agent waits for after a restart (FR-CYC-8, NFR-REL-3).
/// It answers beside <see cref="OrderReconciliation"/>, and an agent passes when both say so.
/// See <see cref="IReconciliation"/> and "After a restart" in docs/cycles.md.
/// </summary>
/// <remarks>
/// It answers at once, from what <see cref="AccountReconciliation"/> remembers and from the rows.
/// An agent that cannot be reconciled, because the key of its user is gone or the exchange does
/// not answer for its account, gets no cycle, and keeps no other agent waiting.
/// </remarks>
internal sealed class ReconciledSinceStart(AccountReconciliation reconciliation) : IReconciliation
{
    public Task<bool> HasReconciledAsync(RunningAgent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return reconciliation.HasReconciledAsync(agent.Agent.Id, cancellationToken);
    }
}
