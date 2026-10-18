namespace Ecnanib.Exchange.Streaming;

/// <summary>A live socket subscription: connection events plus reconnect and close. Faked in tests.</summary>
internal abstract class StreamSubscriptionHandle : IAsyncDisposable
{
    public event Action? ConnectionLost;

    public event Action<TimeSpan>? ConnectionRestored;

    /// <summary>The library gave up on this subscription; it must be created again.</summary>
    public event Action? ConnectionClosed;

    /// <summary>The socket reconnected but re-subscribing failed.</summary>
    public event Action? ResubscribeFailed;

    public abstract Task ReconnectAsync(CancellationToken ct);

    public abstract Task CloseAsync();

    public virtual ValueTask DisposeAsync() => new(CloseAsync());

    protected void RaiseConnectionLost() => ConnectionLost?.Invoke();

    protected void RaiseConnectionRestored(TimeSpan offline) => ConnectionRestored?.Invoke(offline);

    protected void RaiseConnectionClosed() => ConnectionClosed?.Invoke();

    protected void RaiseResubscribeFailed() => ResubscribeFailed?.Invoke();
}
