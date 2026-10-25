using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Exchange.Streaming;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Exchange.Streaming;

public class TickStreamSubscriptionTests
{
    [Fact]
    public async Task PollsAtFixedIntervalWhileDegradedOnly()
    {
        var time = new FakeTimeProvider();
        var source = new FakeMarketStreamSource();
        var key = new MarketKey(ExchangeEnvironment.Production, MarketType.UsdFutures, "ETHUSDT");
        var options = new ExchangeStreamOptions { PricePollIntervalSeconds = 5 };
        await using var subscription = new TickStreamSubscription<PriceTick>(
            "price", (h, ct) => source.SubscribePriceAsync(key, h, ct), ct => source.PollPriceAsync(key, ct), TimeSpan.FromSeconds(5), options, time, NullLogger.Instance);
        var received = 0;
        subscription.AddHandler((_, _) => { Interlocked.Increment(ref received); return ValueTask.CompletedTask; });
        subscription.Start();
        await Wait.UntilAsync(() => subscription.Status.Health == StreamHealth.Connected);

        source.PriceCallback!(new PriceTick("ETHUSDT", 2m, time.GetUtcNow()));
        await Wait.UntilAsync(() => received == 1);
        await Wait.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        await Wait.SettleAsync();
        Assert.Equal(0, source.PollCalls);

        source.LastHandle.SimulateLost();
        await Wait.UntilAsync(() => subscription.Status.Health == StreamHealth.Polling);
        await Wait.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await Wait.UntilAsync(() => source.PollCalls == 1);
        await Wait.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await Wait.UntilAsync(() => source.PollCalls == 2);
        await Wait.UntilAsync(() => received == 3);

        source.LastHandle.SimulateRestored(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => subscription.Status.Health == StreamHealth.Connected);
        await Wait.UntilAsync(() => source.PollCalls == 3, "gap-fill poll on restore");
        await Wait.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        await Wait.SettleAsync();
        Assert.Equal(3, source.PollCalls);
    }
}

public class MarketStreamHubTests
{
    private static MarketStreamHub CreateHub(FakeMarketStreamSource source, FakeTimeProvider time) =>
        new(source, Options.Create(new ExchangeOptions()), time, NullLoggerFactory.Instance);

    [Fact]
    public async Task SubscribersShareOneStreamAndTheLastOneClosesIt()
    {
        var source = new FakeMarketStreamSource();
        var hub = CreateHub(source, new FakeTimeProvider());
        var key = new MarketKey(ExchangeEnvironment.Testnet, MarketType.Spot, "btcusdt");

        var a = hub.SubscribeCandles(key, CandleInterval.OneHour, (_, _) => ValueTask.CompletedTask);
        var b = hub.SubscribeCandles(key, CandleInterval.OneHour, (_, _) => ValueTask.CompletedTask);
        await Wait.UntilAsync(() => source.SubscribeCalls == 1);
        await Wait.SettleAsync();
        Assert.Equal(1, source.SubscribeCalls);
        Assert.Single(hub.GetStatuses());
        Assert.Equal("candles:testnet:spot:btcusdt:1h", hub.GetStatuses()[0].Name);

        a.Dispose();
        a.Dispose();
        await Wait.SettleAsync();
        Assert.False(source.LastHandle.Closed);
        Assert.Single(hub.GetStatuses());

        b.Dispose();
        await Wait.UntilAsync(() => source.LastHandle.Closed, "closed after last unsubscribe");
        Assert.Empty(hub.GetStatuses());

        hub.SubscribeCandles(key, CandleInterval.OneHour, (_, _) => ValueTask.CompletedTask);
        await Wait.UntilAsync(() => source.SubscribeCalls == 2, "reopened");
        await hub.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(hub.GetStatuses());
    }

    [Fact]
    public async Task UpdateSubscribersShareTheCandleStreamAndKeepItOpen()
    {
        var source = new FakeMarketStreamSource();
        var hub = CreateHub(source, new FakeTimeProvider());
        var key = new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT");
        var updates = 0;

        var closed = hub.SubscribeCandles(key, CandleInterval.FifteenMinutes, (_, _) => ValueTask.CompletedTask);
        var forming = hub.SubscribeCandleUpdates(key, CandleInterval.FifteenMinutes, (_, _) => { Interlocked.Increment(ref updates); return ValueTask.CompletedTask; });
        await Wait.UntilAsync(() => source.SubscribeCalls == 1 && source.CandleCallback is not null);
        Assert.Single(hub.GetStatuses());

        var open = DateTimeOffset.Parse("2026-10-18T10:00:00Z");
        source.CandleCallback!(new Candle("BTCUSDT", CandleInterval.FifteenMinutes, open, open.AddMinutes(15), 1, 2, 0.5m, 1.5m, 10, 15, 3, false));
        await Wait.UntilAsync(() => Volatile.Read(ref updates) == 1, "forming candle delivered to the update handler");

        closed.Dispose();
        await Wait.SettleAsync();
        Assert.False(source.LastHandle.Closed);
        Assert.Single(hub.GetStatuses());

        forming.Dispose();
        await Wait.UntilAsync(() => source.LastHandle.Closed, "closed after the last update handler left");
        Assert.Empty(hub.GetStatuses());
        await hub.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void MarkPriceOnSpotIsRejected()
    {
        var hub = CreateHub(new FakeMarketStreamSource(), new FakeTimeProvider());
        Assert.Throws<NotSupportedException>(() =>
            hub.SubscribeMarkPrice(new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT"), (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task PriceAndMarkStreamsHaveDistinctNames()
    {
        var source = new FakeMarketStreamSource();
        var hub = CreateHub(source, new FakeTimeProvider());
        var key = new MarketKey(ExchangeEnvironment.Production, MarketType.UsdFutures, "ETHUSDT");
        using var p = hub.SubscribePrice(key, (_, _) => ValueTask.CompletedTask);
        using var m = hub.SubscribeMarkPrice(key, (_, _) => ValueTask.CompletedTask);
        await Wait.UntilAsync(() => source.SubscribeCalls == 2);

        Assert.Equal(["mark:production:usdfutures:ethusdt", "price:production:usdfutures:ethusdt"], hub.GetStatuses().Select(s => s.Name));
        await hub.StopAsync(TestContext.Current.CancellationToken);
    }
}
