namespace Ecnanib.Tests.Support;

/// <summary>Deterministic <see cref="TimeProvider"/>: time only moves when a test calls <see cref="Advance"/>, firing due timers in order.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public int ActiveTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => t.DueAt.HasValue);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void SetUtcNow(DateTimeOffset now) => Advance(now - GetUtcNow());

    /// <summary>Moves time forward, firing every timer that becomes due, in due order, with the clock set to each due time.</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by));

        var target = GetUtcNow() + by;
        while (true)
        {
            FakeTimer? next;
            lock (_lock)
            {
                next = _timers
                    .Where(t => t.DueAt.HasValue && t.DueAt.Value <= target)
                    .OrderBy(t => t.DueAt!.Value)
                    .ThenBy(t => t.Sequence)
                    .FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.DueAt!.Value;
                next.Reschedule();
            }

            next.Fire();
        }
    }

    private void Register(FakeTimer timer)
    {
        lock (_lock)
        {
            if (!_timers.Contains(timer))
                _timers.Add(timer);
        }
    }

    private void Unregister(FakeTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private static long _sequenceSource;
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            Sequence = Interlocked.Increment(ref _sequenceSource);
        }

        public long Sequence { get; }

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._lock)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    return true;
                }

                DueAt = _owner._now + dueTime;
                _owner.Register(this);
                return true;
            }
        }

        /// <summary>Called under the owner's lock just before firing.</summary>
        public void Reschedule()
        {
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
                DueAt = DueAt!.Value + _period;
            else
                DueAt = null;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._lock)
            {
                DueAt = null;
                _owner.Unregister(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
