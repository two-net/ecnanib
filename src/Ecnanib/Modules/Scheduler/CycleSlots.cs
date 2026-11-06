namespace Ecnanib.Modules.Scheduler;

/// <summary>
/// Limits how many cycles run at once, and queues the rest fairly (FR-CYC-08). A waiter with an earlier deadline goes
/// first, so a 15m cycle goes before a 1D one when their closes coincide. Then users take turns: a waiter's round is
/// how many cycles its user already had queued or running when it arrived, so one user's agents can't crowd out
/// another's. Then whoever arrived first. A waiter still queued at its deadline gives up, because it could enter
/// nothing. In memory, which is enough for one instance (ASM-06).
/// </summary>
internal sealed class CycleSlots(SchedulerSettings settings, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly PriorityQueue<Waiter, (DateTimeOffset Deadline, int Round, long Arrival)> _waiting = new();
    private readonly Dictionary<Guid, int> _perUser = [];
    private int _free = settings.MaxConcurrentCycles;
    private long _arrivals;

    /// <summary>A slot, or null if <paramref name="deadline"/> passed first.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired first.</exception>
    public async Task<Slot?> WaitAsync(Guid userId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        Waiter waiter;

        lock (_lock)
        {
            var round = _perUser.GetValueOrDefault(userId);
            _perUser[userId] = round + 1;

            // A free slot means nobody is waiting: Release hands a slot on before it frees one.
            if (_free > 0)
            {
                _free--;

                return new Slot(this, userId);
            }

            waiter = new Waiter(userId, deadline);
            _waiting.Enqueue(waiter, (deadline, round, _arrivals++));
        }

        // The timer gives up on time even if no slot frees, and Release checks again, because a timer can be late.
        var remaining = deadline - time.GetUtcNow();
        using var expiry = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);
        await using var registration = either.Token.Register(() => GiveUp(waiter));

        if (await waiter.Granted.Task)
        {
            return new Slot(this, userId);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return null;
    }

    private void GiveUp(Waiter waiter)
    {
        lock (_lock)
        {
            GiveUpLocked(waiter);
        }
    }

    /// <summary>Hands the slot to the first waiter that's still due, or frees it.</summary>
    private void Release(Guid userId)
    {
        lock (_lock)
        {
            Leave(userId);

            var now = time.GetUtcNow();

            while (_waiting.TryDequeue(out var waiter, out _))
            {
                if (waiter.Deadline <= now)
                {
                    GiveUpLocked(waiter);
                    continue;
                }

                if (waiter.Granted.TrySetResult(true))
                {
                    return;
                }
            }

            _free++;
        }
    }

    private void GiveUpLocked(Waiter waiter)
    {
        if (waiter.Granted.TrySetResult(false))
        {
            Leave(waiter.UserId);
        }
    }

    private void Leave(Guid userId)
    {
        if (--_perUser[userId] == 0)
        {
            _perUser.Remove(userId);
        }
    }

    /// <summary>A queued cycle. A waiter that gave up stays in the queue until it's dequeued, and is skipped then.</summary>
    private sealed class Waiter(Guid userId, DateTimeOffset deadline)
    {
        public Guid UserId { get; } = userId;

        public DateTimeOffset Deadline { get; } = deadline;

        /// <summary>True when it's given a slot, false when it gives up. Whichever comes first wins.</summary>
        public TaskCompletionSource<bool> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One running cycle's place. Disposing it, once the cycle's run has ended, passes it on.</summary>
    internal sealed class Slot(CycleSlots slots, Guid userId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release(userId);
            }
        }
    }
}
