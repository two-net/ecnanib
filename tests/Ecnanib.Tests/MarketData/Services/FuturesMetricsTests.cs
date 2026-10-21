using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Services;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.MarketData.Services;

/// <summary>Mark price, funding rate and next funding time per followed futures symbol (FR-DATA-04).</summary>
public class FuturesMetricsTests
{
    private static readonly MarketKey Btc = new(ExchangeEnvironment.Production, MarketType.UsdFutures, "BTCUSDT");
    private static readonly DateTimeOffset At = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LeasesShareOneStreamAndTheLastReleaseClosesIt()
    {
        var streams = new FakeMarketStreams();
        var metrics = new FuturesMetrics(streams, NullLogger<FuturesMetrics>.Instance);

        Assert.Null(metrics.Latest(Btc));
        var first = metrics.Acquire(Btc);
        var second = metrics.Acquire(Btc);
        Assert.Equal(1, streams.MarkSubscriptions);
        Assert.True(streams.HasMarkSubscription(Btc));
        Assert.Null(metrics.Latest(Btc));

        await streams.PushMarkPriceAsync(Btc, FakeMarketStreams.Mark("BTCUSDT", 50_000m, 0.0001m, At));
        await streams.PushMarkPriceAsync(Btc, FakeMarketStreams.Mark("BTCUSDT", 50_100m, 0.0002m, At.AddSeconds(3)));
        var latest = metrics.Latest(Btc);
        Assert.Equal(50_100m, latest!.Mark);
        Assert.Equal(0.0002m, latest.FundingRate);
        Assert.Equal(At.AddSeconds(3).AddHours(8), latest.NextFundingTime);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, streams.MarkSubscriptions);
        Assert.NotNull(metrics.Latest(Btc));
        second.Dispose();
        Assert.Equal(0, streams.MarkSubscriptions);
        Assert.Null(metrics.Latest(Btc));
        Assert.Equal(0, metrics.Followed);
    }

    [Fact]
    public void AStreamThatCannotBeOpenedIsToleratedAndASpotKeyIsRefused()
    {
        var logs = new TestLoggerProvider();
        var streams = new FakeMarketStreams { MarkSubscribeError = new InvalidOperationException("socket limit") };
        var metrics = new FuturesMetrics(streams, TestLogging.CreateFactory(logs).CreateLogger<FuturesMetrics>());

        using var lease = metrics.Acquire(Btc);

        Assert.Null(metrics.Latest(Btc));
        Assert.Equal(1, metrics.Followed);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be opened", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => metrics.Acquire(Btc with { Market = MarketType.Spot }));
    }
}
