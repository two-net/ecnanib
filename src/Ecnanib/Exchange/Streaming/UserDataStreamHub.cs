using System.Collections.Concurrent;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange.Streaming;

/// <summary>Owns one <see cref="UserDataStreamSession"/> per (key, market), reference-counted like market streams.</summary>
internal sealed class UserDataStreamHub : BackgroundService, IUserDataStreams
{
    private readonly IUserDataSource _source;
    private readonly IOptions<ExchangeOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<UserDataStreamHub> _logger;
    private readonly ConcurrentDictionary<(string KeyId, MarketType Market), UserDataStreamSession> _sessions = new();
    private readonly object _lock = new();

    public UserDataStreamHub(IUserDataSource source, IOptions<ExchangeOptions> options, TimeProvider time, ILoggerFactory loggerFactory)
    {
        _source = source;
        _options = options;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<UserDataStreamHub>();
    }

    public IDisposable Subscribe(ExchangeKeyHandle key, MarketType market, Func<UserDataEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            var session = _sessions.GetOrAdd((key.KeyId, market), _ =>
            {
                _logger.LogInformation("Opening user-data session for key {KeyId} ({MaskedApiKey}) on {Market}", key.KeyId, key.Credentials.MaskedApiKey, market);
                return new UserDataStreamSession(key, market, _source, _options.Value.Streams, _time, _loggerFactory.CreateLogger<UserDataStreamSession>());
            });
            var registration = session.AddHandler(handler);
            session.Start();
            return new Unsubscriber(() => Release(key.KeyId, market, session, registration));
        }
    }

    public StreamStatus GetStatus(string keyId, MarketType market)
    {
        if (_sessions.TryGetValue((keyId, market), out var session))
            return session.Status;
        var now = _time.GetUtcNow();
        return new StreamStatus($"userdata:{market}:{keyId}".ToLowerInvariant(), StreamHealth.Stopped, null, now, null);
    }

    public IReadOnlyList<StreamStatus> GetStatuses() =>
        _sessions.Values.Select(s => s.Status).OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

    public async Task StopSessionsAsync(string keyId, CancellationToken ct)
    {
        var removed = new List<UserDataStreamSession>();
        lock (_lock)
        {
            foreach (var entry in _sessions.Where(e => e.Key.KeyId == keyId).ToArray())
            {
                if (_sessions.TryRemove(entry.Key, out var session))
                    removed.Add(session);
            }
        }

        foreach (var session in removed)
        {
            _logger.LogInformation("Closing user-data session {Name}: key released", session.Name);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(key, out var session))
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Release(string keyId, MarketType market, UserDataStreamSession session, IDisposable registration)
    {
        UserDataStreamSession? toDispose = null;
        lock (_lock)
        {
            registration.Dispose();
            if (session.HandlerCount == 0 && _sessions.TryRemove(new KeyValuePair<(string, MarketType), UserDataStreamSession>((keyId, market), session)))
                toDispose = session;
        }

        if (toDispose is not null)
        {
            _logger.LogInformation("Closing user-data session {Name}: no subscribers left", session.Name);
            _ = toDispose.DisposeAsync().AsTask().ContinueWith(
                t => _logger.LogWarning(t.Exception, "Closing user-data session {Name} failed", session.Name),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private sealed class Unsubscriber(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                release();
        }
    }
}
