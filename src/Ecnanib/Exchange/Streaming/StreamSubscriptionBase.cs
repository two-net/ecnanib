using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;

namespace Ecnanib.Exchange.Streaming;

/// <summary>
/// One shared market stream with automatic reconnection and REST polling fallback (EX-03, FR-DATA-03). A single
/// supervisor loop owns the socket subscription, a silence watchdog and the polling schedule; all timing goes through
/// <see cref="TimeProvider"/> so the machine is testable without wall-clock waits.
/// </summary>
internal abstract class StreamSubscriptionBase<T> : IAsyncDisposable
{
    private readonly ExchangeStreamOptions _options;
    private readonly SubscriptionDispatcher<T> _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private Task? _runner;
    private StreamSubscriptionHandle? _handle;
    private TaskCompletionSource _wake = NewWake();
    private volatile bool _connected;
    private volatile bool _stateChanged;
    private volatile bool _pendingGapFill;
    private volatile bool _reconnectRequested;
    private volatile bool _handleClosed;
    private volatile bool _lastPollFailed;
    private volatile bool _stopped;
    private long _lastMessageTicks;
    private string? _lastError;

    protected StreamSubscriptionBase(string name, ExchangeStreamOptions options, TimeProvider time, ILogger logger)
    {
        Name = name;
        _options = options;
        Time = time;
        Logger = logger;
        Since = time.GetUtcNow();
        _dispatcher = new SubscriptionDispatcher<T>(name, logger);
    }

    public string Name { get; }

    protected TimeProvider Time { get; }

    protected ILogger Logger { get; }

    protected ExchangeStreamOptions Options => _options;

    public DateTimeOffset Since { get; }

    public bool IsConnected => _connected;

    public int HandlerCount => _dispatcher.HandlerCount;

    public DateTimeOffset? LastMessageAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastMessageTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public StreamStatus Status
    {
        get
        {
            var health = _stopped ? StreamHealth.Stopped
                : _connected ? StreamHealth.Connected
                : _lastPollFailed ? StreamHealth.Down
                : StreamHealth.Polling;
            return new StreamStatus(Name, health, LastMessageAt, Since, Volatile.Read(ref _lastError));
        }
    }

    public IDisposable AddHandler(Func<T, CancellationToken, ValueTask> handler) => _dispatcher.AddHandler(handler);

    public void Start()
    {
        lock (_lock)
        {
            _runner ??= Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        Wake();
        if (_runner is { } runner)
        {
            try
            {
                await runner.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Stream {Name} supervisor ended with an error", Name);
            }
        }

        await CloseHandleAsync().ConfigureAwait(false);
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>Creates the socket subscription and wires <see cref="OnStreamUpdate"/>.</summary>
    protected abstract Task<StreamSubscriptionHandle> SubscribeAsync(CancellationToken ct);

    /// <summary>Fetches the data the stream would have delivered and hands it to <see cref="Deliver"/>.</summary>
    protected abstract Task PollAsync(CancellationToken ct);

    /// <summary>When the next poll is due; the schedule differs while connected (safety checks only) and while degraded.</summary>
    protected abstract DateTimeOffset NextPollAt(DateTimeOffset now, bool connected);

    /// <summary>Whether a due poll should run even though the stream is connected (late or missing updates).</summary>
    protected abstract bool ShouldPollWhenConnected(DateTimeOffset now);

    /// <summary>Filter applied to every item, from stream or poll, before delivery (used for candle dedupe).</summary>
    protected virtual bool Accept(T item) => true;

    protected void OnStreamUpdate(T item)
    {
        Interlocked.Exchange(ref _lastMessageTicks, Time.GetUtcNow().UtcTicks);
        Deliver(item);
    }

    protected void Deliver(T item)
    {
        if (Accept(item))
            _dispatcher.TryPublish(item);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _options.ReconnectInitialDelay;
        DateTimeOffset? nextSubscribeAt = Time.GetUtcNow();
        DateTimeOffset? nextReconnectAt = null;
        var nextPollAt = NextPollAt(Time.GetUtcNow(), connected: false);

        while (!ct.IsCancellationRequested)
        {
            var wakeTask = ResetWake();
            var now = Time.GetUtcNow();

            if (_handleClosed)
            {
                _handleClosed = false;
                await CloseHandleAsync().ConfigureAwait(false);
                nextSubscribeAt ??= now + backoff;
                backoff = Grow(backoff);
            }

            if (_handle is null && nextSubscribeAt is { } due && due <= now)
            {
                try
                {
                    var handle = await SubscribeAsync(ct).ConfigureAwait(false);
                    Attach(handle);
                    nextSubscribeAt = null;
                    backoff = _options.ReconnectInitialDelay;
                    SetConnected(true, now);
                    Volatile.Write(ref _lastError, null);
                    Logger.LogInformation("Stream {Name} subscribed", Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastError, ex.Message);
                    nextSubscribeAt = now + backoff;
                    backoff = Grow(backoff);
                    SetConnected(false, now);
                    Logger.LogWarning(ex, "Stream {Name} could not subscribe; retrying in {Delay} and polling meanwhile", Name, nextSubscribeAt - now);
                }
            }

            if (_reconnectRequested && _handle is { } current)
            {
                nextReconnectAt ??= now + _options.ReconnectInitialDelay;
                if (nextReconnectAt <= now)
                {
                    _reconnectRequested = false;
                    nextReconnectAt = null;
                    try
                    {
                        await current.ReconnectAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Stream {Name} reconnect failed", Name);
                    }
                }
            }

            if (_connected && LastMessageAt is { } last && now - last > _options.NoDataTimeout)
            {
                Logger.LogWarning("Stream {Name} received nothing for {Silence}; reconnecting and polling meanwhile", Name, now - last);
                Volatile.Write(ref _lastError, $"No data for {now - last:g}.");
                SetConnected(false, now);
                _reconnectRequested = true;
                nextReconnectAt = now;
            }

            if (_stateChanged)
            {
                _stateChanged = false;
                nextPollAt = NextPollAt(now, _connected);
            }

            var connected = _connected;
            var pollDue = nextPollAt <= now;
            if (_pendingGapFill || (pollDue && (!connected || ShouldPollWhenConnected(now))))
            {
                _pendingGapFill = false;
                try
                {
                    await PollAsync(ct).ConfigureAwait(false);
                    _lastPollFailed = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _lastPollFailed = true;
                    Volatile.Write(ref _lastError, ex.Message);
                    Logger.LogWarning(ex, "Stream {Name} poll failed", Name);
                }

                now = Time.GetUtcNow();
                nextPollAt = NextPollAt(now, _connected);
            }
            else if (pollDue)
            {
                nextPollAt = NextPollAt(now, connected);
            }

            var wakeAt = nextPollAt;
            if (nextSubscribeAt is { } s && s < wakeAt)
                wakeAt = s;
            if (_reconnectRequested && nextReconnectAt is { } r && r < wakeAt)
                wakeAt = r;
            if (connected)
            {
                var watchdogAt = now + WatchdogPeriod();
                if (watchdogAt < wakeAt)
                    wakeAt = watchdogAt;
            }

            var delay = wakeAt - now;
            if (delay > TimeSpan.Zero)
                await Task.WhenAny(Task.Delay(delay, Time, ct), wakeTask).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    private TimeSpan WatchdogPeriod()
    {
        var half = TimeSpan.FromTicks(_options.NoDataTimeout.Ticks / 2);
        return half < TimeSpan.FromSeconds(30) ? half : TimeSpan.FromSeconds(30);
    }

    private TimeSpan Grow(TimeSpan backoff)
    {
        var doubled = TimeSpan.FromTicks(backoff.Ticks * 2);
        return doubled > _options.ReconnectMaxDelay ? _options.ReconnectMaxDelay : doubled;
    }

    private void Attach(StreamSubscriptionHandle handle)
    {
        handle.ConnectionLost += OnConnectionLost;
        handle.ConnectionRestored += OnConnectionRestored;
        handle.ConnectionClosed += OnConnectionClosed;
        handle.ResubscribeFailed += OnResubscribeFailed;
        lock (_lock)
        {
            _handle = handle;
        }
    }

    private async Task CloseHandleAsync()
    {
        StreamSubscriptionHandle? handle;
        lock (_lock)
        {
            handle = _handle;
            _handle = null;
        }

        if (handle is null)
            return;

        handle.ConnectionLost -= OnConnectionLost;
        handle.ConnectionRestored -= OnConnectionRestored;
        handle.ConnectionClosed -= OnConnectionClosed;
        handle.ResubscribeFailed -= OnResubscribeFailed;
        try
        {
            await handle.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Stream {Name} close failed", Name);
        }
    }

    private void SetConnected(bool connected, DateTimeOffset now)
    {
        if (connected)
            Interlocked.Exchange(ref _lastMessageTicks, now.UtcTicks);
        if (_connected != connected)
        {
            _connected = connected;
            _stateChanged = true;
        }
    }

    private void OnConnectionLost()
    {
        Logger.LogWarning("Stream {Name} lost its connection; polling until it is restored", Name);
        SetConnected(false, Time.GetUtcNow());
        Wake();
    }

    private void OnConnectionRestored(TimeSpan offline)
    {
        Logger.LogInformation("Stream {Name} restored after {Offline}; filling the gap", Name, offline);
        SetConnected(true, Time.GetUtcNow());
        _pendingGapFill = true;
        Wake();
    }

    private void OnConnectionClosed()
    {
        Logger.LogWarning("Stream {Name} was closed by the library; subscribing again", Name);
        SetConnected(false, Time.GetUtcNow());
        _handleClosed = true;
        Wake();
    }

    private void OnResubscribeFailed()
    {
        Logger.LogWarning("Stream {Name} could not re-subscribe after reconnecting; retrying", Name);
        SetConnected(false, Time.GetUtcNow());
        _reconnectRequested = true;
        Wake();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task ResetWake()
    {
        var fresh = NewWake();
        Interlocked.Exchange(ref _wake, fresh);
        return fresh.Task;
    }

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();
}
