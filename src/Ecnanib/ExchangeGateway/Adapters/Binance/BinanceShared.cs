namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// What is read from Binance once and shared by everyone who asks for it (FR-BIN-3): for each
/// thing that can be asked for, the one read that is under way, or what it gave for as long as
/// that counts. See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// Whoever asks first causes the read. Whoever asks while it is under way waits for the same
/// read, and whoever asks while what it gave still counts gets that, with no call. After that the
/// next ask causes a read again. Nothing is read without an ask.
/// </para>
/// <para>
/// A read belongs to nobody who waits for it: it runs until the application stops, and an asker
/// that cancels stops waiting, and ends nothing for the others.
/// </para>
/// <para>
/// What no longer counts is given to nobody, which is looked at with every ask, and is let go
/// of when its time is up, by a timer, without waiting for an ask (NFR-DAT-6). It is kept in
/// memory, for this process, and nowhere else.
/// </para>
/// </remarks>
/// <typeparam name="TKey">What can be asked for: a symbol on a market in an environment, for example.</typeparam>
/// <typeparam name="TValue">What a read gives, which is given to every asker as it is, and so does not change.</typeparam>
internal sealed class BinanceShared<TKey, TValue>(TimeProvider clock, CancellationToken stopping) : IDisposable
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly long _began = clock.GetTimestamp();
    private readonly Dictionary<TKey, Held> _held = [];

    private ITimer? _timer;
    private TimeSpan? _wakesAt;
    private bool _disposed;

    /// <summary>Gives what is held for a key, or what the read that is under way gives, or reads.</summary>
    /// <param name="key">What is asked for.</param>
    /// <param name="read">
    /// Reads it from Binance, and says for how long what it read counts. It is told when the
    /// application stops, and not when an asker cancels.
    /// </param>
    /// <param name="cancellationToken">Ends the wait of this asker.</param>
    /// <exception cref="OperationCanceledException">The asker cancelled, or the application stops.</exception>
    public Task<TValue> AskAsync(TKey key, Func<CancellationToken, Task<BinanceRead<TValue>>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Held held;
        TaskCompletionSource<TValue>? begun = null;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_held.TryGetValue(key, out held!) || !Counts(held))
            {
                begun = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                held = new Held(begun.Task);
                _held[key] = held;
            }
        }

        // Outside of the gate: the read may go a long way before it first waits.
        if (begun is not null)
        {
            _ = ReadAsync(key, held, begun, read);
        }

        return held.Reading.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _held.Clear();
            _timer?.Dispose();
        }
    }

    /// <summary>Makes the one read for a key, and gives everyone who waits for it what it gave.</summary>
    private async Task ReadAsync(
        TKey key, Held held, TaskCompletionSource<TValue> begun, Func<CancellationToken, Task<BinanceRead<TValue>>> read)
    {
        BinanceRead<TValue> gave;

        try
        {
            gave = await read(stopping);
        }
        catch (Exception error)
        {
            // Nothing to hold. Who waited is told, and the next ask reads again.
            Forget(key, held);

            if (error is OperationCanceledException cancelled)
            {
                begun.TrySetCanceled(cancelled.CancellationToken);
            }
            else
            {
                begun.TrySetException(error);
            }

            return;
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                var now = Now();
                var counts = gave.CountsFor;

                // Not past a time of day either, where the read names one. It is worked out
                // once, here: after that the hold is counted in time that no change of the time
                // of day moves, and the time of day is looked at again with each ask.
                if (gave.Until is { } until && until - clock.GetUtcNow() is var left && left < counts)
                {
                    counts = left;
                }

                held.Until = gave.Until;
                held.Ends = now + counts;

                if (counts > TimeSpan.Zero)
                {
                    Wake(held.Ends.Value, now);
                }
                else if (_held.TryGetValue(key, out var current) && ReferenceEquals(current, held))
                {
                    // It is over as it arrives: given to who waited, and not held.
                    _held.Remove(key);
                }
            }
        }

        begun.TrySetResult(gave.Value);
    }

    /// <summary>
    /// Whether what is held still counts: a read that is under way does, and what it gave does
    /// until its time is up. Called with the gate held.
    /// </summary>
    private bool Counts(Held held) =>
        held.Ends is not { } ends || (Now() < ends && (held.Until is not { } until || clock.GetUtcNow() < until));

    /// <summary>Lets go of a read that gave nothing.</summary>
    private void Forget(TKey key, Held held)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(key, out var current) && ReferenceEquals(current, held))
            {
                _held.Remove(key);
            }
        }
    }

    /// <summary>Lets go of everything whose time is up, and sets the timer to the next that is. Called by the timer.</summary>
    private void Release()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = Now();
            TimeSpan? next = null;

            foreach (var (key, held) in _held.ToList())
            {
                if (held.Ends is not { } ends)
                {
                    continue;
                }

                if (ends <= now)
                {
                    _held.Remove(key);
                }
                else if (next is null || ends < next)
                {
                    next = ends;
                }
            }

            _wakesAt = null;

            if (next is { } due)
            {
                Wake(due, now);
            }
        }
    }

    /// <summary>Sets the timer to a moment, when it is not set to an earlier one. Called with the gate held.</summary>
    private void Wake(TimeSpan at, TimeSpan now)
    {
        if (_wakesAt is { } already && already <= at)
        {
            return;
        }

        var due = at - now;
        if (due < TimeSpan.FromMilliseconds(1))
        {
            due = TimeSpan.FromMilliseconds(1);
        }

        _timer ??= clock.CreateTimer(_ => Release(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(due, Timeout.InfiniteTimeSpan);
        _wakesAt = at;
    }

    /// <summary>The time since this began, which no change of the time of day moves.</summary>
    private TimeSpan Now() => clock.GetElapsedTime(_began);

    /// <summary>The one read for a key, and until when what it gave counts.</summary>
    private sealed class Held(Task<TValue> reading)
    {
        /// <summary>Ends when the read does, with what it gave.</summary>
        public Task<TValue> Reading { get; } = reading;

        /// <summary>Until when it counts, in the time since this began, or null while the read is under way.</summary>
        public TimeSpan? Ends { get; set; }

        /// <summary>The time of day that it does not count past, where the read named one.</summary>
        public DateTimeOffset? Until { get; set; }
    }
}

/// <summary>What one read of something that is shared gave.</summary>
/// <param name="Value">What was read, or how the read ended.</param>
/// <param name="CountsFor">For how long it is given to whoever asks, with no read. Nothing or less is not at all.</param>
/// <param name="Until">The time of day that it is not given past, when there is one: the close of a candle.</param>
internal readonly record struct BinanceRead<TValue>(TValue Value, TimeSpan CountsFor, DateTimeOffset? Until = null);
