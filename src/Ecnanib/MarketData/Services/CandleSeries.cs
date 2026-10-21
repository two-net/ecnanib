using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.MarketData.Services;

/// <summary>
/// One series the feed holds: the closed candles of a symbol and interval, ascending, at most <see cref="Capacity"/>
/// of them. Every write inserts by open time and keeps the first value, so the backfill, the stream and the loader
/// may arrive in any order; a hole between two candles is recorded after every write so the feed can repair it. One
/// load (backfill or repair) runs at a time; callers share the one in flight.
/// </summary>
internal sealed class CandleSeries
{
    /// <summary>
    /// Candles kept beyond the configured depth, so an evaluation of a candle that is no longer the newest still sees
    /// a full window. How many candles can close while an evaluation of an older one is still running is zero in
    /// normal operation (a deadline is shorter than its timeframe); this is the margin for the exceptions.
    /// </summary>
    public const int Slack = 8;

    private static readonly Comparer<Candle> ByOpenTime = Comparer<Candle>.Create((a, b) => a.OpenTime.CompareTo(b.OpenTime));

    private readonly object _lock = new();
    private readonly object _loadLock = new();
    private readonly List<Candle> _candles = [];
    private Task? _load;
    private Hole? _hole;
    private bool _loaded;
    private bool _exhausted;

    public CandleSeries(MarketKey key, CandleInterval interval, int depth)
    {
        Key = key;
        Interval = interval;
        Depth = depth;
        Capacity = depth + Slack;
        Span = interval.ToTimeSpan();
    }

    public MarketKey Key { get; }

    public CandleInterval Interval { get; }

    /// <summary>The configured backfill depth, which is also the window every evaluation sees.</summary>
    public int Depth { get; }

    public int Capacity { get; }

    public TimeSpan Span { get; }

    /// <summary>The agents using the series; guarded by the feed's lock.</summary>
    public HashSet<Guid> Users { get; } = [];

    /// <summary>Cancelled when the series is released; the backfill and repairs run under it.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The hub subscription; set by the feed under its lock.</summary>
    public IDisposable? Subscription { get; set; }

    public bool Disposed { get; private set; }

    /// <summary>True once a backfill has completed.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_lock)
                return _loaded;
        }
    }

    /// <summary>True when the exchange has no more contiguous history: the last backfill returned fewer candles than the depth, or candles with a hole of its own.</summary>
    public bool Exhausted
    {
        get
        {
            lock (_lock)
                return _exhausted;
        }
    }

    /// <summary>The range from the first missing candle to the last, or null when the series is contiguous.</summary>
    public Hole? Hole
    {
        get
        {
            lock (_lock)
                return _hole;
        }
    }

    public bool NeedsRepair => Hole is not null;

    /// <summary>Backfilled, contiguous and holding the depth, unless the exchange has no more to give.</summary>
    public bool IsReady
    {
        get
        {
            lock (_lock)
                return _loaded && _hole is null && (_candles.Count >= Depth || _exhausted);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _candles.Count;
        }
    }

    public Candle? First
    {
        get
        {
            lock (_lock)
                return _candles.Count == 0 ? null : _candles[0];
        }
    }

    public Candle? Last
    {
        get
        {
            lock (_lock)
                return _candles.Count == 0 ? null : _candles[^1];
        }
    }

    public bool Contains(DateTimeOffset openTime)
    {
        lock (_lock)
            return IndexOf(openTime) >= 0;
    }

    /// <summary>Inserts one closed candle by open time; a candle already held, one older than the first while the series is full, or one still forming is left out.</summary>
    public InsertOutcome Insert(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        lock (_lock)
        {
            var outcome = InsertLocked(candle);
            if (outcome == InsertOutcome.Inserted)
                SettleLocked();
            return outcome;
        }
    }

    /// <summary>Inserts every candle not yet held; returns the ones inserted, ascending.</summary>
    public IReadOnlyList<Candle> Merge(IEnumerable<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var inserted = new List<Candle>();
        lock (_lock)
        {
            foreach (var candle in candles.OrderBy(c => c.OpenTime))
            {
                if (InsertLocked(candle) == InsertOutcome.Inserted)
                    inserted.Add(candle);
            }

            if (inserted.Count > 0)
                SettleLocked();
        }

        return inserted;
    }

    /// <summary>Records a completed backfill; <paramref name="exhausted"/> says the exchange returned less than the depth of contiguous history.</summary>
    public void MarkLoaded(bool exhausted)
    {
        lock (_lock)
        {
            _loaded = true;
            _exhausted = exhausted;
        }
    }

    /// <summary>Whether the candles follow each other without a gap, in open-time order.</summary>
    public static bool IsContiguous(IEnumerable<Candle> candles, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(candles);
        DateTimeOffset? previous = null;
        foreach (var candle in candles.OrderBy(c => c.OpenTime))
        {
            if (previous is { } last && candle.OpenTime != last + span)
                return false;
            previous = candle.OpenTime;
        }

        return true;
    }

    /// <summary>Drops every candle before the last hole, so the series is contiguous again; returns how many were dropped.</summary>
    public int KeepContiguousTail()
    {
        lock (_lock)
        {
            var last = LastHoleIndexLocked();
            if (last < 0)
                return 0;
            var dropped = last + 1;
            _candles.RemoveRange(0, dropped);
            _hole = null;
            return dropped;
        }
    }

    /// <summary>The last <paramref name="count"/> candles with an open time at or before <paramref name="upToOpen"/>.</summary>
    public IReadOnlyList<Candle> Window(DateTimeOffset upToOpen, int count)
    {
        lock (_lock)
        {
            var end = UpperBoundLocked(upToOpen);
            var start = Math.Max(0, end - count);
            return _candles.GetRange(start, end - start);
        }
    }

    /// <summary>The last <paramref name="count"/> candles that closed at or before <paramref name="cutoff"/> (no look-ahead, FR-STR-03).</summary>
    public IReadOnlyList<Candle> WindowClosedBy(DateTimeOffset cutoff, int count) => Window(cutoff - Span, count);

    /// <summary>
    /// Runs <paramref name="work"/> under the series' token unless a load is already in flight, which is then
    /// shared; a load that finished is never reused, so a failed one is tried again. The caller stops waiting on its
    /// own token while the load goes on.
    /// </summary>
    public Task RunExclusiveAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        TaskCompletionSource started;
        lock (_loadLock)
        {
            if (_load is { IsCompleted: false } running)
                return running.WaitAsync(cancellationToken);
            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _load = started.Task;
        }

        _ = RunAsync(work, started);
        return started.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        Disposed = true;
        Cancellation.Cancel();
        Subscription?.Dispose();
        Subscription = null;
    }

    public override string ToString() => $"{Key} {Interval.ToLabel()}";

    private async Task RunAsync(Func<CancellationToken, Task> work, TaskCompletionSource started)
    {
        try
        {
            await work(Cancellation.Token).ConfigureAwait(false);
            started.SetResult();
        }
        catch (Exception ex)
        {
            started.SetException(ex);
        }
    }

    private InsertOutcome InsertLocked(Candle candle)
    {
        if (!candle.IsClosed)
            return InsertOutcome.NotClosed;
        var index = _candles.BinarySearch(candle, ByOpenTime);
        if (index >= 0)
            return InsertOutcome.Duplicate;
        index = ~index;
        if (index == 0 && _candles.Count >= Capacity)
            return InsertOutcome.Older;
        _candles.Insert(index, candle);
        return InsertOutcome.Inserted;
    }

    /// <summary>Trims to the capacity from the front and records the hole, if any, after a write.</summary>
    private void SettleLocked()
    {
        if (_candles.Count > Capacity)
            _candles.RemoveRange(0, _candles.Count - Capacity);

        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        for (var i = 0; i + 1 < _candles.Count; i++)
        {
            var expected = _candles[i].OpenTime + Span;
            if (_candles[i + 1].OpenTime <= expected)
                continue;
            from ??= expected;
            to = _candles[i + 1].OpenTime - Span;
        }

        _hole = from is null ? null : new Hole(from.Value, to!.Value, (int)((to.Value - from.Value) / Span) + 1);
    }

    private int LastHoleIndexLocked()
    {
        for (var i = _candles.Count - 2; i >= 0; i--)
        {
            if (_candles[i + 1].OpenTime > _candles[i].OpenTime + Span)
                return i;
        }

        return -1;
    }

    private int IndexOf(DateTimeOffset openTime)
    {
        var low = 0;
        var high = _candles.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var comparison = _candles[mid].OpenTime.CompareTo(openTime);
            if (comparison == 0)
                return mid;
            if (comparison < 0)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return -1;
    }

    /// <summary>The index of the first candle opening after <paramref name="openTime"/>.</summary>
    private int UpperBoundLocked(DateTimeOffset openTime)
    {
        var low = 0;
        var high = _candles.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_candles[mid].OpenTime <= openTime)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }
}

/// <summary>From the first missing open time to the last; <see cref="Missing"/> counts the candles of that range, held or not.</summary>
internal readonly record struct Hole(DateTimeOffset From, DateTimeOffset To, int Missing);

internal enum InsertOutcome
{
    Inserted,
    Duplicate,
    Older,
    NotClosed,
}
