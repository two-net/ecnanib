namespace Ecnanib.Modules.AiAdapters;

/// <summary>
/// Limits concurrent calls per user credential (EXT-AI-03), so one agent's burst waits on its own key and
/// never on someone else's (NFR-REL-05). Each gate exists only while something holds or waits for it, so
/// the dictionary doesn't grow with every credential ever used.
/// </summary>
internal sealed class CredentialGates
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Provider, Guid CredentialId), Gate> _gates = [];

    /// <summary>
    /// Waits for a slot, which the caller disposes to give it back. The limit is the provider's, so it's the
    /// same for every caller of a gate.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    public async Task<IDisposable> EnterAsync(string provider, Guid credentialId, int limit, CancellationToken cancellationToken)
    {
        var key = (provider, credentialId);
        Gate gate;

        lock (_lock)
        {
            if (!_gates.TryGetValue(key, out gate!))
            {
                gate = new Gate(limit);
                _gates[key] = gate;
            }

            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, gate);
            throw;
        }

        return new Slot(this, key, gate);
    }

    private void Leave((string, Guid) key, Gate gate)
    {
        lock (_lock)
        {
            // Nobody else holds or waits for it, and anyone new would increment Users under this lock
            // first, so it's safe to drop. Someone arriving later gets a new gate.
            if (--gate.Users == 0)
            {
                _gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class Gate(int limit)
    {
        public SemaphoreSlim Semaphore { get; } = new(limit, limit);

        /// <summary>Holders and waiters, guarded by the dictionary's lock.</summary>
        public int Users { get; set; }
    }

    private sealed class Slot(CredentialGates owner, (string, Guid) key, Gate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Semaphore.Release();
                owner.Leave(key, gate);
            }
        }
    }
}
