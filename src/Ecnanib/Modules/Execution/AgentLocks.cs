using System.Collections.Concurrent;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// One holder at a time for an agent's orders: the cycle's execution, its reconciliation, the agent's watch, or a
/// cancellation. Whoever
/// holds it reads the rows afresh, so no two of them ever place a leg for the same fill (NFR-REL-03). It's in memory,
/// which is enough for one instance (ASM-06).
/// </summary>
internal sealed class AgentLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Waits for the agent's lock. Dispose the result to release it.</summary>
    public async Task<IDisposable> HoldAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        return new Held(gate);
    }

    /// <summary>The agent's lock if nobody holds it, or null. Dispose the result to release it.</summary>
    public IDisposable? TryHold(Guid agentId)
    {
        var gate = _locks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));

        return gate.Wait(0) ? new Held(gate) : null;
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
