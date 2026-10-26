using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;

namespace Ecnanib.Exchange.Streaming;

/// <summary>
/// One key's user-data stream on one market. While the socket is unavailable a REST snapshot is polled instead; every
/// loss, restore and snapshot is published so the execution module can reconcile (FR-EXE-08).
/// </summary>
internal sealed class UserDataStreamSession : IAsyncDisposable
{
    private readonly ExchangeKeyHandle _key;
    private readonly MarketType _market;
    private readonly IUserDataSource _source;
    private readonly ExchangeStreamOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SubscriptionDispatcher<UserDataEvent> _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private Task? _runner;
    private StreamSubscriptionHandle? _handle;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _connected;
    private volatile bool _pendingSnapshot;
    private volatile bool _handleTerminated;
    private volatile bool _lastPollFailed;
    private volatile bool _stopped;
    private long _lastMessageTicks;
    private long _lostAtTicks;
    private string? _lastError;

    public UserDataStreamSession(ExchangeKeyHandle key, MarketType market, IUserDataSource source, ExchangeStreamOptions options, TimeProvider time, ILogger logger)
    {
        _key = key;
        _market = market;
        _source = source;
        _options = options;
        _time = time;
        _logger = logger;
        Name = NameFor(key.Environment, market, key.KeyId);
        Since = time.GetUtcNow();
        _dispatcher = new SubscriptionDispatcher<UserDataEvent>(Name, logger);
    }

    public static string NameFor(ExchangeEnvironment environment, MarketType market, string keyId) =>
        $"userdata:{environment}:{market}:{keyId}".ToLowerInvariant();

    public string Name { get; }

    public DateTimeOffset Since { get; }

    public int HandlerCount => _dispatcher.HandlerCount;

    public StreamStatus Status
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastMessageTicks);
            var health = _stopped ? StreamHealth.Stopped
                : _connected ? StreamHealth.Connected
                : _lastPollFailed ? StreamHealth.Down
                : StreamHealth.Polling;
            return new StreamStatus(Name, health, ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero), Since, Volatile.Read(ref _lastError));
        }
    }

    public IDisposable AddHandler(Func<UserDataEvent, CancellationToken, ValueTask> handler) => _dispatcher.AddHandler(handler);

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
                _logger.LogError(ex, "User-data session {Name} supervisor ended with an error", Name);
            }
        }

        await CloseHandleAsync().ConfigureAwait(false);
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _options.ReconnectInitialDelay;
        DateTimeOffset? nextSubscribeAt = _time.GetUtcNow();
        var nextPollAt = _time.GetUtcNow();

        while (!ct.IsCancellationRequested)
        {
            var wakeTask = ResetWake();
            var now = _time.GetUtcNow();

            if (_handleTerminated)
            {
                _handleTerminated = false;
                await CloseHandleAsync().ConfigureAwait(false);
                nextSubscribeAt ??= now + backoff;
                backoff = Grow(backoff);
            }

            if (_handle is null && nextSubscribeAt is { } due && due <= now)
            {
                try
                {
                    var handle = await _source.SubscribeAsync(_key, _market, OnEvent, OnTerminated, ct).ConfigureAwait(false);
                    Attach(handle);
                    nextSubscribeAt = null;
                    backoff = _options.ReconnectInitialDelay;
                    SetConnected(true, now);
                    Volatile.Write(ref _lastError, null);
                    _logger.LogInformation("User-data session {Name} subscribed", Name);
                    TryMarkRestored(now, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastError, ErrorSummary.Describe(ex));
                    nextSubscribeAt = now + backoff;
                    backoff = Grow(backoff);
                    SetConnected(false, now);
                    TryMarkLost(now);
                    _logger.LogWarning(ex, "User-data session {Name} could not subscribe; retrying in {Delay} and polling meanwhile", Name, nextSubscribeAt - now);
                }
            }

            var connected = _connected;
            if (_pendingSnapshot || (!connected && nextPollAt <= now))
            {
                _pendingSnapshot = false;
                try
                {
                    var snapshot = await _source.SnapshotAsync(_key, _market, ct).ConfigureAwait(false);
                    Publish(snapshot);
                    _lastPollFailed = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _lastPollFailed = true;
                    Volatile.Write(ref _lastError, ErrorSummary.Describe(ex));
                    _logger.LogWarning(ex, "User-data session {Name} snapshot failed", Name);
                }

                now = _time.GetUtcNow();
                nextPollAt = now + _options.UserDataPollInterval;
            }
            else if (nextPollAt <= now)
            {
                nextPollAt = now + _options.UserDataPollInterval;
            }

            var wakeAt = connected ? DateTimeOffset.MaxValue : nextPollAt;
            if (nextSubscribeAt is { } s && s < wakeAt)
                wakeAt = s;
            if (wakeAt == DateTimeOffset.MaxValue)
            {
                await wakeTask.ConfigureAwait(false);
            }
            else
            {
                var delay = wakeAt - now;
                if (delay > TimeSpan.Zero)
                    await Task.WhenAny(Task.Delay(delay, _time, ct), wakeTask).ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Publishes <see cref="UserDataEvent.StreamLost"/> once per outage, whichever path detects it first.</summary>
    private void TryMarkLost(DateTimeOffset now)
    {
        if (Interlocked.CompareExchange(ref _lostAtTicks, now.UtcTicks, 0) == 0)
            Publish(new UserDataEvent.StreamLost(_key.KeyId, _market, now));
    }

    /// <summary>Publishes <see cref="UserDataEvent.StreamRestored"/> once per outage and schedules the reconciliation snapshot.</summary>
    private void TryMarkRestored(DateTimeOffset now, TimeSpan? offline)
    {
        var lostTicks = Interlocked.Exchange(ref _lostAtTicks, 0);
        if (lostTicks == 0)
            return;

        var duration = offline ?? now - new DateTimeOffset(lostTicks, TimeSpan.Zero);
        Publish(new UserDataEvent.StreamRestored(_key.KeyId, _market, now, duration));
        _pendingSnapshot = true;
    }

    private TimeSpan Grow(TimeSpan backoff)
    {
        var doubled = TimeSpan.FromTicks(backoff.Ticks * 2);
        return doubled > _options.ReconnectMaxDelay ? _options.ReconnectMaxDelay : doubled;
    }

    private void Publish(UserDataEvent e) => _dispatcher.TryPublish(e);

    private void OnEvent(UserDataEvent e)
    {
        Interlocked.Exchange(ref _lastMessageTicks, _time.GetUtcNow().UtcTicks);
        Publish(e);
    }

    private void OnTerminated()
    {
        _logger.LogWarning("User-data session {Name} was terminated by the exchange; subscribing again", Name);
        var now = _time.GetUtcNow();
        SetConnected(false, now);
        TryMarkLost(now);
        _pendingSnapshot = true;
        _handleTerminated = true;
        Wake();
    }

    private void OnConnectionLost()
    {
        _logger.LogWarning("User-data session {Name} lost its connection; polling snapshots until it is restored", Name);
        var now = _time.GetUtcNow();
        SetConnected(false, now);
        TryMarkLost(now);
        _pendingSnapshot = true;
        Wake();
    }

    private void OnConnectionRestored(TimeSpan offline)
    {
        _logger.LogInformation("User-data session {Name} restored after {Offline}", Name, offline);
        var now = _time.GetUtcNow();
        SetConnected(true, now);
        TryMarkRestored(now, offline);
        Wake();
    }

    private void OnConnectionClosed() => OnTerminated();

    private void OnResubscribeFailed()
    {
        _logger.LogWarning("User-data session {Name} could not re-subscribe; recreating the stream", Name);
        OnTerminated();
    }

    private void SetConnected(bool connected, DateTimeOffset now)
    {
        if (connected)
            Interlocked.Exchange(ref _lastMessageTicks, now.UtcTicks);
        _connected = connected;
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
            _logger.LogDebug(ex, "User-data session {Name} close failed", Name);
        }
    }

    private Task ResetWake()
    {
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _wake, fresh);
        return fresh.Task;
    }

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();
}
