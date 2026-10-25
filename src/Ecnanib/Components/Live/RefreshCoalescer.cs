using System.Threading.Channels;

namespace Ecnanib.Components.Live;

/// <summary>
/// The refresh loop behind every live island (FR-UI-04, NFR-PERF-03). Requests collapse into one re-read after a short
/// debounce, a request that arrives during a read yields one trailing read so nothing is missed, and the time from the
/// first request of a burst to the end of its render is measured against the latency budget: a Warning when it is
/// exceeded, Debug otherwise. The clock is the <see cref="TimeProvider"/>, so a test drives the loop with a fake one.
/// The measurement covers the server side only; the SignalR hop to the browser is not in it.
/// </summary>
internal sealed class RefreshCoalescer
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Lock _lock = new();
    private readonly Func<CancellationToken, Task> _load;
    private readonly Func<Task> _render;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _component;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _budget;
    private bool _pending;
    private long _firstRequestAt;
    private TimeSpan? _lastLatency;
    private int _refreshes;

    public RefreshCoalescer(Func<CancellationToken, Task> load, Func<Task> render, TimeProvider time, ILogger logger, string component, TimeSpan debounce, TimeSpan latencyBudget)
    {
        _load = load;
        _render = render;
        _time = time;
        _logger = logger;
        _component = component;
        _debounce = debounce;
        _budget = latencyBudget;
    }

    /// <summary>From the first request of the last burst to the end of its render; null before the first refresh.</summary>
    public TimeSpan? LastLatency
    {
        get
        {
            lock (_lock)
                return _lastLatency;
        }
    }

    /// <summary>Re-reads that completed with their render.</summary>
    public int Refreshes
    {
        get
        {
            lock (_lock)
                return _refreshes;
        }
    }

    /// <summary>Asks for a re-read; several requests in flight collapse into one. The first request of a burst stamps the burst.</summary>
    public void Request()
    {
        lock (_lock)
        {
            if (!_pending)
            {
                _pending = true;
                _firstRequestAt = _time.GetTimestamp();
            }

            _signal.Writer.TryWrite(true);
        }
    }

    /// <summary>No request is served after this; the loop ends once the pending one is done.</summary>
    public void Complete() => _signal.Writer.TryComplete();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var requested in _signal.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = requested;
                await Task.Delay(_debounce, _time, cancellationToken).ConfigureAwait(false);
                long start;
                lock (_lock)
                {
                    // Every request made up to here is served by the read that follows; a later one stamps a new burst
                    // and yields one trailing read.
                    while (_signal.Reader.TryRead(out _))
                    {
                    }

                    start = _firstRequestAt;
                    _pending = false;
                }

                try
                {
                    await _load(cancellationToken).ConfigureAwait(false);
                    await _render().ConfigureAwait(false);
                    Measure(start);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException ex) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Live view {Component} refreshed after its circuit ended", _component);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Live view {Component} could not refresh; the next event tries again", _component);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Measure(long start)
    {
        var latency = _time.GetElapsedTime(start);
        lock (_lock)
        {
            _lastLatency = latency;
            _refreshes++;
        }

        if (latency > _budget)
            _logger.LogWarning("Live view {Component} took {Latency} to reflect an event; the budget is {Budget} (NFR-PERF-03)", _component, latency, _budget);
        else
            _logger.LogDebug("Live view {Component} reflected an event in {Latency}", _component, latency);
    }
}
