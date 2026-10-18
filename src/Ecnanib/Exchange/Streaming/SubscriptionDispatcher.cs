using System.Threading.Channels;

namespace Ecnanib.Exchange.Streaming;

/// <summary>
/// Decouples exchange callbacks from consumers: items are queued on a channel and handed to the handlers one at a time
/// by a single reader, so a slow consumer never stalls the socket and a throwing consumer never kills the stream.
/// </summary>
internal sealed class SubscriptionDispatcher<T> : IAsyncDisposable
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Registration> _handlers = [];
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly Task _loop;

    public SubscriptionDispatcher(string name, ILogger logger)
    {
        _name = name;
        _logger = logger;
        _loop = Task.Run(RunAsync);
    }

    public int HandlerCount
    {
        get
        {
            lock (_lock)
            {
                return _handlers.Count;
            }
        }
    }

    public IDisposable AddHandler(Func<T, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = new Registration(this, handler);
        lock (_lock)
        {
            _handlers.Add(registration);
        }

        return registration;
    }

    public bool TryPublish(T item) => _channel.Writer.TryWrite(item);

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Registration[] handlers;
                lock (_lock)
                {
                    handlers = _handlers.ToArray();
                }

                foreach (var registration in handlers)
                {
                    try
                    {
                        await registration.Handler(item, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "A handler of stream {Name} failed; the stream continues", _name);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Remove(Registration registration)
    {
        lock (_lock)
        {
            _handlers.Remove(registration);
        }
    }

    private sealed class Registration(SubscriptionDispatcher<T> owner, Func<T, CancellationToken, ValueTask> handler) : IDisposable
    {
        private int _disposed;

        public Func<T, CancellationToken, ValueTask> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Remove(this);
        }
    }
}
