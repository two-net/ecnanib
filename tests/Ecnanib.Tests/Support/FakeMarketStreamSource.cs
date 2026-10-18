using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Tests.Support;

internal sealed class FakeMarketStreamSource : IMarketStreamSource
{
    private readonly object _lock = new();
    private int _subscribeCalls;
    private int _pollCalls;

    public List<FakeStreamSubscriptionHandle> Handles { get; } = [];

    public List<(DateTimeOffset? From, int Limit)> CandlePollRequests { get; } = [];

    public Action<Candle>? CandleCallback { get; private set; }

    public Action<PriceTick>? PriceCallback { get; private set; }

    public Action<MarkPrice>? MarkCallback { get; private set; }

    public Exception? SubscribeError { get; set; }

    public Exception? PollError { get; set; }

    public Func<DateTimeOffset?, int, IReadOnlyList<Candle>> CandlePoll { get; set; } = (_, _) => [];

    public Func<PriceTick> PricePoll { get; set; } = () => new PriceTick("BTCUSDT", 1m, DateTimeOffset.UnixEpoch);

    public Func<MarkPrice> MarkPoll { get; set; } = () => new MarkPrice("BTCUSDT", 1m, 1m, 0m, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public int SubscribeCalls => Volatile.Read(ref _subscribeCalls);

    public int PollCalls => Volatile.Read(ref _pollCalls);

    public FakeStreamSubscriptionHandle LastHandle
    {
        get
        {
            lock (_lock)
            {
                return Handles[^1];
            }
        }
    }

    public Task<StreamSubscriptionHandle> SubscribeCandlesAsync(MarketKey key, CandleInterval interval, Action<Candle> onUpdate, CancellationToken ct)
    {
        CandleCallback = onUpdate;
        return SubscribeAsync();
    }

    public Task<StreamSubscriptionHandle> SubscribePriceAsync(MarketKey key, Action<PriceTick> onTick, CancellationToken ct)
    {
        PriceCallback = onTick;
        return SubscribeAsync();
    }

    public Task<StreamSubscriptionHandle> SubscribeMarkPriceAsync(MarketKey key, Action<MarkPrice> onMark, CancellationToken ct)
    {
        MarkCallback = onMark;
        return SubscribeAsync();
    }

    public Task<IReadOnlyList<Candle>> PollCandlesAsync(MarketKey key, CandleInterval interval, DateTimeOffset? fromOpenTime, int limit, CancellationToken ct)
    {
        Interlocked.Increment(ref _pollCalls);
        lock (_lock)
        {
            CandlePollRequests.Add((fromOpenTime, limit));
        }

        if (PollError is { } error)
            throw error;
        return Task.FromResult(CandlePoll(fromOpenTime, limit));
    }

    public Task<PriceTick> PollPriceAsync(MarketKey key, CancellationToken ct)
    {
        Interlocked.Increment(ref _pollCalls);
        if (PollError is { } error)
            throw error;
        return Task.FromResult(PricePoll());
    }

    public Task<MarkPrice> PollMarkPriceAsync(MarketKey key, CancellationToken ct)
    {
        Interlocked.Increment(ref _pollCalls);
        if (PollError is { } error)
            throw error;
        return Task.FromResult(MarkPoll());
    }

    private Task<StreamSubscriptionHandle> SubscribeAsync()
    {
        Interlocked.Increment(ref _subscribeCalls);
        if (SubscribeError is { } error)
            throw error;
        var handle = new FakeStreamSubscriptionHandle();
        lock (_lock)
        {
            Handles.Add(handle);
        }

        return Task.FromResult<StreamSubscriptionHandle>(handle);
    }
}

internal sealed class FakeUserDataSource : IUserDataSource
{
    private int _subscribeCalls;
    private int _snapshotCalls;

    public List<FakeStreamSubscriptionHandle> Handles { get; } = [];

    public Action<UserDataEvent>? OnEvent { get; private set; }

    public Action? OnTerminated { get; private set; }

    public Exception? SubscribeError { get; set; }

    public Exception? SnapshotError { get; set; }

    public int SubscribeCalls => Volatile.Read(ref _subscribeCalls);

    public int SnapshotCalls => Volatile.Read(ref _snapshotCalls);

    public Task<StreamSubscriptionHandle> SubscribeAsync(ExchangeKeyHandle key, MarketType market, Action<UserDataEvent> onEvent, Action onTerminated, CancellationToken ct)
    {
        Interlocked.Increment(ref _subscribeCalls);
        if (SubscribeError is { } error)
            throw error;
        OnEvent = onEvent;
        OnTerminated = onTerminated;
        var handle = new FakeStreamSubscriptionHandle();
        Handles.Add(handle);
        return Task.FromResult<StreamSubscriptionHandle>(handle);
    }

    public Task<UserDataEvent.Snapshot> SnapshotAsync(ExchangeKeyHandle key, MarketType market, CancellationToken ct)
    {
        Interlocked.Increment(ref _snapshotCalls);
        if (SnapshotError is { } error)
            throw error;
        return Task.FromResult(new UserDataEvent.Snapshot(key.KeyId, market, DateTimeOffset.UnixEpoch, [], null, []));
    }
}
