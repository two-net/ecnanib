using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Services;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.MarketData.Live;

/// <summary>FR-DATA-02 and FR-DATA-05 against PostgreSQL inside the real scheduler and pipeline, with the exchange and streams faked.</summary>
[Trait("Category", "Live")]
public class CandleFeedLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset LastOpen = new(2026, 1, 4, 23, 0, 0, TimeSpan.Zero);
    private readonly DatabaseLiveFixture _fixture;

    public CandleFeedLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BackfillAndStreamedClosesArePersistedOnceAndOnlyInsideTheRetentionPeriod()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "persist@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var (provider, logs, time, exchange, streams) = AgentsLiveSeed.Build(_fixture,
            new() { ["Retention:CandleDays"] = "2", ["MarketData:BackfillCandles:1H"] = "60", ["MarketData:BackfillCandles:1D"] = "30" }, marketData: true);
        await using var _ = provider;
        exchange.OnRecentCandles = (symbol, interval, count) => Recent(symbol, interval, count, time.GetUtcNow());
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var feed = provider.GetRequiredService<CandleFeed>();
        await using var cleanup = new Cleanup(_fixture, scheduler, user.Id);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");
        await Wait.UntilAsync(() => feed.Series.Count == 2 && feed.Series.All(s => s.IsReady), "backfilled");

        // Two days of retention from 2026-01-05 00:00:05: the hourly candles from 01:00 on the 3rd and the daily candle of the 4th.
        await Wait.UntilAsync(() => Stored(CandleInterval.OneHour) == 47 && Stored(CandleInterval.OneDay) == 1, "persisted", timeoutMs: 10000);
        await using (var db = _fixture.CreateContext())
        {
            var rows = await db.Candles.AsNoTracking().Where(c => c.Symbol == "BTCUSDT").ToListAsync(Ct);
            Assert.DoesNotContain(rows, c => c.OpenTime < AgentsLiveSeed.Now.AddDays(-2));
            Assert.Contains(rows, c => c.Interval == CandleInterval.OneHour && c.OpenTime == LastOpen);
            Assert.All(rows, c => Assert.Equal(ExchangeEnvironment.Production, c.Environment));
        }

        // The next close arrives twice (the hub's safety poll repeats the newest candle): stored once, evaluated once.
        time.Advance(TimeSpan.FromHours(1));
        var next = FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.OneHour, LastOpen.AddHours(1), 101m);
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, next);
        await streams.PushCandleAsync(AgentsLiveSeed.SpotBtc, CandleInterval.OneHour, next);
        await Wait.UntilAsync(() => Stored(CandleInterval.OneHour) == 48, "streamed close persisted");
        await Wait.UntilAsync(() => Evaluations(agent.Id) == 1, "evaluated", timeoutMs: 20000);
        await Wait.SettleAsync(200);
        Assert.Equal(48, Stored(CandleInterval.OneHour));
        Assert.Equal(1, Evaluations(agent.Id));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("could not be stored", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Category.StartsWith("Ecnanib", StringComparison.Ordinal) && e.Level >= LogLevel.Error);

        await scheduler.StopAsync(Ct);
        Assert.Equal(0, streams.CandleSubscriptions);
        Assert.Empty(feed.Series);
    }

    [Fact]
    public async Task InsideTheSchedulerAnEvaluationSeesTheConfiguredWindowWithoutReadingTheExchange()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "window@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "ETH", AgentState.Running, market: MarketType.UsdFutures, symbol: "ETHUSDT");
        var tracker = new CapturingTracker();
        var (provider, logs, time, exchange, streams) = AgentsLiveSeed.Build(_fixture,
            new() { ["MarketData:BackfillCandles:1H"] = "60", ["MarketData:BackfillCandles:1D"] = "30" },
            configure: s => s.Replace(ServiceDescriptor.Singleton<IExperienceTracker>(tracker)), marketData: true);
        await using var _ = provider;
        exchange.OnRecentCandles = (symbol, interval, count) => Recent(symbol, interval, count, time.GetUtcNow());
        exchange.OnMarkPrice = symbol => new MarkPrice(symbol, 100.5m, null, 0.0001m, AgentsLiveSeed.Now.AddHours(8), AgentsLiveSeed.Now);
        var ethFutures = new MarketKey(ExchangeEnvironment.Production, MarketType.UsdFutures, "ETHUSDT");
        var scheduler = provider.GetRequiredService<AgentScheduler>();
        var feed = provider.GetRequiredService<CandleFeed>();
        await using var cleanup = new Cleanup(_fixture, scheduler, user.Id);
        await scheduler.StartAsync(Ct);
        await Wait.UntilAsync(() => scheduler.IsTracked(agent.Id), "tracked");
        await Wait.UntilAsync(() => feed.Series.Count == 2 && feed.Series.All(s => s.IsReady), "backfilled");
        Assert.True(streams.HasMarkSubscription(ethFutures));

        await streams.PushMarkPriceAsync(ethFutures, FakeMarketStreams.Mark("ETHUSDT", 102m, 0.0003m, AgentsLiveSeed.Now));
        await streams.PushCandleAsync(ethFutures, CandleInterval.OneHour, FakeMarketStreams.ClosedCandle("ETHUSDT", CandleInterval.OneHour, LastOpen, 101m));
        // A generous wait: the real pipeline makes several database round trips, slow when the whole suite runs at once.
        await Wait.UntilAsync(() => Evaluations(agent.Id) == 1, "evaluated", timeoutMs: 20000);
        Assert.NotNull(tracker.Context);

        var market = tracker.Context!.Market;
        Assert.Equal(60, market.Candles.Count);
        Assert.Equal(LastOpen, market.Latest!.OpenTime);
        Assert.Equal(30, market.Higher(CandleInterval.OneDay).Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero), market.Higher(CandleInterval.OneDay)[^1].OpenTime);
        Assert.Equal(0.0003m, market.MarkPrice!.FundingRate);
        Assert.Equal(2, exchange.RecentCandleReads.Count);
        Assert.Empty(exchange.CandleReads);
        await using (var db = _fixture.CreateContext())
        {
            var evaluation = await db.Evaluations.AsNoTracking().SingleAsync(e => e.AgentId == agent.Id, Ct);
            Assert.NotNull(evaluation.FinishedAt);
            Assert.DoesNotContain("insufficient history", evaluation.OutcomeDetail ?? "", StringComparison.Ordinal);
        }

        Assert.DoesNotContain(logs.Entries, e => e.Category.StartsWith("Ecnanib", StringComparison.Ordinal) && e.Level >= LogLevel.Error);
        await scheduler.StopAsync(Ct);
        Assert.Equal(0, streams.CandleSubscriptions);
        Assert.Equal(0, streams.MarkSubscriptions);
    }

    private int Stored(CandleInterval interval)
    {
        using var db = _fixture.CreateContext();
        return db.Candles.AsNoTracking().Count(c => c.Symbol == "BTCUSDT" && c.Interval == interval);
    }

    private int Evaluations(Guid agentId)
    {
        using var db = _fixture.CreateContext();
        return db.Evaluations.AsNoTracking().Count(e => e.AgentId == agentId && e.FinishedAt != null);
    }

    /// <summary>The newest <paramref name="count"/> closed candles before <paramref name="now"/>.</summary>
    private static IReadOnlyList<Candle> Recent(string symbol, CandleInterval interval, int count, DateTimeOffset now)
    {
        var lastOpen = interval.OpenTimeOf(now) - interval.ToTimeSpan();
        return Enumerable.Range(0, count).Reverse().Select(i => FakeMarketStreams.ClosedCandle(symbol, interval, lastOpen - i * interval.ToTimeSpan(), 100m)).ToList();
    }

    /// <summary>Stops the scheduler and deletes the test's user even when an assertion failed, so the class's shared database stays clean for the next test.</summary>
    private sealed class Cleanup(DatabaseLiveFixture fixture, AgentScheduler scheduler, Guid userId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await scheduler.StopAsync(CancellationToken.None);
            await AgentsLiveSeed.DeleteUsersAsync(fixture, userId);
        }
    }

    private sealed class CapturingTracker : IExperienceTracker
    {
        public ExperienceContext? Context { get; private set; }

        public Task TrackAsync(ExperienceContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.CompletedTask;
        }
    }
}
