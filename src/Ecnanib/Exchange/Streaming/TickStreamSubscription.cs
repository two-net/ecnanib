using Ecnanib.Exchange.Configuration;

namespace Ecnanib.Exchange.Streaming;

/// <summary>Price or mark-price stream: every update is delivered; while degraded the value is polled at a fixed interval.</summary>
internal sealed class TickStreamSubscription<T> : StreamSubscriptionBase<T>
{
    private readonly Func<Action<T>, CancellationToken, Task<StreamSubscriptionHandle>> _subscribe;
    private readonly Func<CancellationToken, Task<T>> _poll;
    private readonly TimeSpan _pollInterval;

    public TickStreamSubscription(
        string name,
        Func<Action<T>, CancellationToken, Task<StreamSubscriptionHandle>> subscribe,
        Func<CancellationToken, Task<T>> poll,
        TimeSpan pollInterval,
        ExchangeStreamOptions options,
        TimeProvider time,
        ILogger logger)
        : base(name, options, time, logger)
    {
        _subscribe = subscribe;
        _poll = poll;
        _pollInterval = pollInterval;
    }

    protected override Task<StreamSubscriptionHandle> SubscribeAsync(CancellationToken ct) => _subscribe(OnStreamUpdate, ct);

    protected override async Task PollAsync(CancellationToken ct) => Deliver(await _poll(ct).ConfigureAwait(false));

    protected override DateTimeOffset NextPollAt(DateTimeOffset now, bool connected) => now + _pollInterval;

    protected override bool ShouldPollWhenConnected(DateTimeOffset now) => false;
}
