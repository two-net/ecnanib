using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace Ecnanib.Logging.Services;

/// <summary>
/// The database end of the Serilog pipeline (FR-LOG-01): a bounded queue of events that <see cref="DatabaseLogWriter"/>
/// drains into <c>log_entries</c>. <see cref="Emit"/> never blocks or throws (FR-LOG-08): when the queue is full the
/// newest event is dropped and counted. Events logged on an asynchronous flow under <see cref="Suppress"/>, which is
/// the writer's own, are ignored, so the sink cannot feed itself with the logs of its own inserts.
/// </summary>
internal sealed class DatabaseLogSink : ILogEventSink
{
    internal const int DefaultCapacity = 10_000;

    private static readonly AsyncLocal<bool> Suppressed = new();

    private readonly Channel<LogEvent> _queue;
    private long _dropped;

    public DatabaseLogSink()
        : this(DefaultCapacity)
    {
    }

    internal DatabaseLogSink(int capacity)
    {
        Capacity = capacity;
        _queue = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    }

    public int Capacity { get; }

    public ChannelReader<LogEvent> Reader => _queue.Reader;

    /// <summary>Events dropped since the writer last took the count.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Whether the current asynchronous flow is the writer's, whose events the sink ignores.</summary>
    public static bool IsSuppressed => Suppressed.Value;

    /// <summary>Marks the current asynchronous flow as the writer's until the result is disposed.</summary>
    public static IDisposable Suppress()
    {
        Suppressed.Value = true;
        return new Restore();
    }

    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (Suppressed.Value)
            return;
        if (!_queue.Writer.TryWrite(logEvent))
            Interlocked.Increment(ref _dropped);
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => Suppressed.Value = false;
    }
}
