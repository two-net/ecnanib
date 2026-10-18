using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Exchange.Streaming;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.Exchange.Streaming;

public class CandleStreamSubscriptionTests
{
    private static readonly MarketKey Key = new(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-18T10:00:05Z");
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static ExchangeStreamOptions Options(int noDataTimeoutSeconds) => new()
    {
        NoDataTimeoutSeconds = noDataTimeoutSeconds, ReconnectInitialDelaySeconds = 2, ReconnectMaxDelaySeconds = 60,
        CandleCloseGraceSeconds = 3, LateCloseGraceSeconds = 10, PricePollIntervalSeconds = 5, MarkPricePollIntervalSeconds = 15,
    };

    private static Candle Closed(string open) => Candle(DateTimeOffset.Parse(open), true);

    private static Candle Candle(DateTimeOffset open, bool closed) =>
        new("BTCUSDT", CandleInterval.FifteenMinutes, open, open + Interval - TimeSpan.FromMilliseconds(1), 1, 2, 0.5m, 1.5m, 10, 15, 3, closed);

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse(time);

    private sealed class Harness : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = new(Start);
        public FakeMarketStreamSource Source { get; } = new();
        public CandleStreamSubscription Subscription { get; }
        public List<Candle> Delivered { get; } = [];

        public Harness(bool start = true, int noDataTimeoutSeconds = 120)
        {
            Subscription = new CandleStreamSubscription(Key, CandleInterval.FifteenMinutes, Source, Options(noDataTimeoutSeconds), Time, NullLogger.Instance);
            Subscription.AddHandler((c, _) => { lock (Delivered) Delivered.Add(c); return ValueTask.CompletedTask; });
            if (start)
                Subscription.Start();
        }

        public int DeliveredCount { get { lock (Delivered) return Delivered.Count; } }

        /// <summary>The exchange as the REST fallback sees it: the last closed candle plus the forming one, by fake time.</summary>
        public void UseRealisticPoll() => Source.CandlePoll = (from, limit) =>
        {
            var forming = CandleClock.OpenTimeOf(Time.GetUtcNow(), CandleInterval.FifteenMinutes);
            var all = new List<Candle>();
            for (var open = forming - 4 * Interval; open < forming; open += Interval)
                all.Add(Candle(open, closed: true));
            all.Add(Candle(forming, closed: false));
            // Binance returns the most recent candles when no start time is given.
            return all.Where(c => from is null || c.OpenTime >= from).TakeLast(limit).ToArray();
        };

        public async Task ConnectedAsync()
        {
            await Wait.UntilAsync(() => Source.SubscribeCalls >= 1 && Subscription.Status.Health == StreamHealth.Connected, "connected");
            await Wait.SettleAsync();
        }

        /// <summary>Lets the supervisor loop arm its timers before fake time moves.</summary>
        public async Task AdvanceToAsync(string time)
        {
            await Wait.SettleAsync();
            Time.Advance(At(time) - Time.GetUtcNow());
        }

        public ValueTask DisposeAsync() => Subscription.DisposeAsync();
    }

    [Fact]
    public async Task DeliversEachClosedCandleOnce()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();

        h.Source.CandleCallback!(Candle(At("2026-10-18T10:00:00Z"), closed: false));
        h.Source.CandleCallback!(Closed("2026-10-18T09:45:00Z"));
        h.Source.CandleCallback!(Closed("2026-10-18T09:45:00Z"));
        await Wait.UntilAsync(() => h.DeliveredCount == 1);
        await Wait.SettleAsync();

        Assert.Equal(1, h.DeliveredCount);
        Assert.Equal(At("2026-10-18T09:45:00Z"), h.Subscription.LastDeliveredOpen);
        Assert.Equal(StreamHealth.Connected, h.Subscription.Status.Health);
        Assert.NotNull(h.Subscription.Status.LastMessageAt);
    }

    [Fact]
    public async Task LostConnectionPollsAtNextBoundaryAndGapFillsOnRestore()
    {
        await using var h = new Harness(noDataTimeoutSeconds: 3600);
        await h.ConnectedAsync();
        h.Source.CandleCallback!(Closed("2026-10-18T09:45:00Z"));
        await Wait.UntilAsync(() => h.DeliveredCount == 1);
        h.UseRealisticPoll();

        h.Source.LastHandle.SimulateLost();
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Polling, "polling");

        await h.AdvanceToAsync("2026-10-18T10:15:02Z");
        await Wait.SettleAsync();
        Assert.Equal(0, h.Source.PollCalls);

        await h.AdvanceToAsync("2026-10-18T10:15:03Z");
        await Wait.UntilAsync(() => h.DeliveredCount == 2, "polled candle delivered");
        Assert.Equal(At("2026-10-18T10:00:00Z"), h.Delivered[1].OpenTime);
        Assert.Equal((At("2026-10-18T10:00:00Z"), 1000), h.Source.CandlePollRequests[0]);

        await h.AdvanceToAsync("2026-10-18T10:31:00Z");
        await Wait.UntilAsync(() => h.DeliveredCount == 3, "second boundary polled");
        Assert.Equal(At("2026-10-18T10:15:00Z"), h.Delivered[2].OpenTime);

        h.Source.LastHandle.SimulateRestored(TimeSpan.FromMinutes(31));
        await Wait.UntilAsync(() => h.Source.PollCalls == 3, "gap-fill poll");
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Connected);
        await Wait.SettleAsync();

        Assert.Equal(3, h.DeliveredCount);
    }

    [Fact]
    public async Task RestoreDeliversMissedCandleExactlyOnceEvenIfStreamRepeatsIt()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();
        h.Source.CandlePoll = (_, _) => [Closed("2026-10-18T09:45:00Z"), Candle(At("2026-10-18T10:00:00Z"), closed: false)];

        h.Source.LastHandle.SimulateLost();
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Polling);
        h.Source.LastHandle.SimulateRestored(TimeSpan.FromSeconds(3));
        await Wait.UntilAsync(() => h.DeliveredCount == 1, "gap fill");
        Assert.Equal((null, 2), h.Source.CandlePollRequests[0]);

        h.Source.CandleCallback!(Closed("2026-10-18T09:45:00Z"));
        await Wait.SettleAsync();

        Assert.Equal(1, h.DeliveredCount);
    }

    [Fact]
    public async Task SilenceTriggersReconnectAndPolling()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();

        await h.AdvanceToAsync("2026-10-18T10:02:10Z");
        await Wait.UntilAsync(() => h.Source.LastHandle.ReconnectCalls == 1, "reconnect");
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Polling);

        Assert.Contains("No data", h.Subscription.Status.LastError);
    }

    [Fact]
    public async Task LateCloseSafetyPollRunsOnlyWhenTheCloseIsMissing()
    {
        await using var h = new Harness(noDataTimeoutSeconds: 3600);
        await h.ConnectedAsync();
        h.UseRealisticPoll();

        // First boundary after subscribing: nothing was delivered yet, so the safety poll seeds the last closed candle.
        await h.AdvanceToAsync("2026-10-18T10:00:09Z");
        await Wait.SettleAsync();
        Assert.Equal(0, h.Source.PollCalls);
        await h.AdvanceToAsync("2026-10-18T10:00:10Z");
        await Wait.UntilAsync(() => h.DeliveredCount == 1, "seed poll");
        Assert.Equal(At("2026-10-18T09:45:00Z"), h.Delivered[0].OpenTime);
        Assert.Equal(StreamHealth.Connected, h.Subscription.Status.Health);

        // The stream stays silent about the 10:00 close: the safety poll at 10:15:10 fetches it.
        await h.AdvanceToAsync("2026-10-18T10:15:10Z");
        await Wait.UntilAsync(() => h.DeliveredCount == 2, "safety poll delivered the missing close");
        Assert.Equal(At("2026-10-18T10:00:00Z"), h.Delivered[1].OpenTime);
        Assert.Equal(2, h.Source.PollCalls);

        // The stream delivers the 10:15 close itself: no safety poll at 10:30:10.
        await h.AdvanceToAsync("2026-10-18T10:30:01Z");
        h.Source.CandleCallback!(Closed("2026-10-18T10:15:00Z"));
        await Wait.UntilAsync(() => h.DeliveredCount == 3);
        await h.AdvanceToAsync("2026-10-18T10:30:12Z");
        await Wait.SettleAsync();

        Assert.Equal(2, h.Source.PollCalls);
    }

    [Fact]
    public async Task SubscribeFailureFallsBackToPollingAndRetriesWithBackoff()
    {
        await using var h = new Harness(start: false);
        h.Source.SubscribeError = new ExchangeUnavailableException("down");
        h.Subscription.Start();

        await Wait.UntilAsync(() => h.Source.SubscribeCalls == 1);
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Polling);
        Assert.Equal("down", h.Subscription.Status.LastError);

        await h.AdvanceToAsync("2026-10-18T10:00:07Z");
        await Wait.UntilAsync(() => h.Source.SubscribeCalls == 2, "first retry after 2 s");
        Assert.Equal("down", h.Subscription.Status.LastError);

        h.Source.SubscribeError = null;
        await h.AdvanceToAsync("2026-10-18T10:00:10Z");
        await Wait.SettleAsync();
        Assert.Equal(2, h.Source.SubscribeCalls);
        await h.AdvanceToAsync("2026-10-18T10:00:11Z");
        await Wait.UntilAsync(() => h.Source.SubscribeCalls == 3, "second retry after 4 s");
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Connected);
        Assert.Null(h.Subscription.Status.LastError);
    }

    [Fact]
    public async Task PollFailureMarksStreamDown()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();
        h.Source.PollError = new ExchangeUnavailableException("rest down");
        h.Source.LastHandle.SimulateLost();
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Polling);

        await h.AdvanceToAsync("2026-10-18T10:15:03Z");
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Down, "down");

        Assert.Equal("rest down", h.Subscription.Status.LastError);
        Assert.Equal(0, h.DeliveredCount);
    }

    [Fact]
    public async Task ClosedByLibraryResubscribes()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();
        var first = h.Source.LastHandle;

        first.SimulateClosed();
        await Wait.UntilAsync(() => first.Closed, "old handle closed");
        await h.AdvanceToAsync("2026-10-18T10:00:07Z");
        await Wait.UntilAsync(() => h.Source.SubscribeCalls == 2, "resubscribed");
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Connected);
    }

    [Fact]
    public async Task ResubscribeFailureRequestsReconnect()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();

        h.Source.LastHandle.SimulateResubscribeFailed();
        await Wait.UntilAsync(() => h.Subscription.Status.Health == StreamHealth.Polling);
        await h.AdvanceToAsync("2026-10-18T10:00:07Z");
        await Wait.UntilAsync(() => h.Source.LastHandle.ReconnectCalls == 1, "reconnect requested");
    }

    [Fact]
    public async Task DisposeClosesHandleAndReportsStopped()
    {
        var h = new Harness();
        await h.ConnectedAsync();
        var handle = h.Source.LastHandle;

        await h.DisposeAsync();

        Assert.True(handle.Closed);
        Assert.Equal(StreamHealth.Stopped, h.Subscription.Status.Health);
    }
}
