using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Services;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.MarketData.Services;

/// <summary>
/// The feed over faked streams and exchange: backfill on track, shared series, the fixed window, gap repair, the
/// higher-timeframe cutoff, the mark price and the fail-safe paths (FR-DATA-02..05, FR-STR-03). Persistence goes to a
/// database that is down here, which the feed logs and survives; the live tests check the rows.
/// </summary>
public class CandleFeedTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 7, 10, 0, 5, TimeSpan.Zero);   // Wednesday 10:00:05
    private static readonly MarketKey EthFutures = new(ExchangeEnvironment.Production, MarketType.UsdFutures, "ETHUSDT");
    private static readonly MarketKey EthTestnet = new(ExchangeEnvironment.Testnet, MarketType.UsdFutures, "ETHUSDT");
    private static readonly MarketKey BtcFutures = new(ExchangeEnvironment.Production, MarketType.UsdFutures, "BTCUSDT");
    private static readonly ReferenceSeries BtcWeekly = new("BTCUSDT", CandleInterval.OneWeek);

    /// <summary>A strategy without higher timeframes, for the tests about the own series.</summary>
    private static readonly StrategyDescriptor Bare = new("bare", "Bare", CandleInterval.OneHour, 1, "bare", [], [], []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 1, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task TrackBackfillsAndFollowsEverySeriesAndAnEvaluationSeesAFixedWindow()
    {
        await using var h = new Harness();
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");

        await h.TrackAsync(agent, EthFutures, "trend-pullback", series: 2);

        Assert.Equal([("ETHUSDT", CandleInterval.OneHour, 30), ("ETHUSDT", CandleInterval.OneDay, 25)], h.Exchange.RecentCandleReads.Select(r => (r.Symbol, r.Interval, r.Count)).OrderBy(r => r.Interval));
        Assert.True(h.Streams.HasCandleSubscription(EthFutures, CandleInterval.OneHour));
        Assert.True(h.Streams.HasCandleSubscription(EthFutures, CandleInterval.OneDay));
        Assert.True(h.Streams.HasMarkSubscription(EthFutures));
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("is followed; backfilling 30 candle(s)", StringComparison.Ordinal));
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("backfilled: 25 candle(s) read, 25 held", StringComparison.Ordinal));

        var closed = Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9));
        var snapshot = await h.Feed.LoadAsync(agent, h.Strategy("trend-pullback"), EthFutures, closed, Ct);

        Assert.Equal(30, snapshot.Candles.Count);
        Assert.Equal(closed.OpenTime, snapshot.Latest!.OpenTime);
        AssertContiguous(snapshot.Candles, CandleInterval.OneHour);
        var daily = snapshot.Higher(CandleInterval.OneDay);
        Assert.Equal(25, daily.Count);
        Assert.Equal(At(6, 0), daily[^1].OpenTime);
        AssertContiguous(daily, CandleInterval.OneDay);
        Assert.Empty(snapshot.References);
        Assert.Null(snapshot.MarkPrice);
        // The window came from the series: no read at evaluation time, and the mark price was tried once over REST.
        Assert.Equal(2, h.Exchange.RecentCandleReads.Count);
        Assert.Empty(h.Exchange.CandleReads);
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Mark price of ETHUSDT", StringComparison.Ordinal));

        // Both backfills were written through (to a database that is down here), without failing anything.
        await Wait.UntilAsync(() => h.Db.Calls >= 2, "persisted");
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be stored", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task AgentsShareSeriesTheLastOneReleasesThemAndChangedNeedsDropWhatIsNoLongerNeeded()
    {
        await using var h = new Harness();
        var a = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        var b = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");

        await h.TrackAsync(a, EthFutures, "trend-pullback", series: 2);
        await h.TrackAsync(b, EthFutures, "trend-pullback", series: 2);
        h.Feed.Track(h.Needs(b, EthFutures, "trend-pullback"));

        Assert.Equal(2, h.Exchange.RecentCandleReads.Count);
        Assert.Equal(2, h.Streams.CandleSubscriptions);
        Assert.Equal(1, h.Streams.MarkSubscriptions);
        Assert.Equal(2, h.Feed.Series.Count);

        h.Feed.Untrack(a.Id);
        Assert.Equal(2, h.Streams.CandleSubscriptions);
        Assert.Equal(1, h.Streams.MarkSubscriptions);

        // b no longer needs the daily series (its strategy is unknown): released at once.
        h.Feed.Track(h.Needs(b, EthFutures, null));
        Assert.Equal(1, h.Streams.CandleSubscriptions);
        Assert.False(h.Streams.HasCandleSubscription(EthFutures, CandleInterval.OneDay));
        Assert.Null(h.Feed.Find(EthFutures, CandleInterval.OneDay));
        Assert.True(h.Streams.HasMarkSubscription(EthFutures));

        h.Feed.Untrack(b.Id);
        h.Feed.Untrack(b.Id);
        Assert.Equal(0, h.Streams.CandleSubscriptions);
        Assert.Equal(0, h.Streams.MarkSubscriptions);
        Assert.Empty(h.Feed.Series);
        Assert.Equal(2, h.Logs.Entries.Count(e => e.Message.StartsWith("Series ", StringComparison.Ordinal) && e.Message.Contains("is no longer followed", StringComparison.Ordinal)));
        Assert.Equal(2, h.Streams.CandleSubscriptionsEver);
    }

    [Fact]
    public async Task ReferenceSeriesComeFromProductionAndAreSharedAcrossEnvironmentsAndWithTheOwnWeekly()
    {
        await using var h = new Harness();
        var eth = h.Agent(EthTestnet, CandleInterval.FourHours, "swing-pullback");
        var btc = h.Agent(BtcFutures, CandleInterval.FourHours, "swing-pullback");

        await h.TrackAsync(eth, EthTestnet, "swing-pullback", series: 4);
        await h.TrackAsync(btc, BtcFutures, "swing-pullback", series: 6);

        Assert.Equal(6, h.Exchange.RecentCandleReads.Count);
        Assert.Equal([ExchangeEnvironment.Testnet, ExchangeEnvironment.Testnet, ExchangeEnvironment.Testnet], h.Exchange.RecentCandleReads.Where(r => r.Symbol == "ETHUSDT").Select(r => r.Environment));
        Assert.All(h.Exchange.RecentCandleReads.Where(r => r.Symbol == "BTCUSDT"), r => Assert.Equal(ExchangeEnvironment.Production, r.Environment));

        var ethSnapshot = await h.Feed.LoadAsync(eth, h.Strategy("swing-pullback"), EthTestnet, Harness.Candle("ETHUSDT", CandleInterval.FourHours, At(7, 4)), Ct);
        Assert.Equal(20, ethSnapshot.Candles.Count);
        Assert.Equal(25, ethSnapshot.Higher(CandleInterval.OneDay).Count);
        Assert.Equal(20, ethSnapshot.Higher(CandleInterval.OneWeek).Count);
        var reference = ethSnapshot.Reference(BtcWeekly);
        Assert.Equal(20, reference.Count);
        Assert.All(reference, c => Assert.Equal("BTCUSDT", c.Symbol));
        Assert.Equal(new DateTimeOffset(2025, 12, 29, 0, 0, 0, TimeSpan.Zero), reference[^1].OpenTime);

        var btcSnapshot = await h.Feed.LoadAsync(btc, h.Strategy("swing-pullback"), BtcFutures, Harness.Candle("BTCUSDT", CandleInterval.FourHours, At(7, 4)), Ct);
        Assert.Same(btcSnapshot.Higher(CandleInterval.OneWeek), btcSnapshot.Reference(BtcWeekly));
        Assert.Equal(6, h.Exchange.RecentCandleReads.Count);
        Assert.Empty(h.Exchange.CandleReads);
    }

    [Fact]
    public async Task AStreamedCloseExtendsTheSeriesAndADuplicateIsIgnored()
    {
        await using var h = new Harness();
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, null, series: 1);
        var series = h.Feed.Find(EthFutures, CandleInterval.OneHour)!;
        await Wait.UntilAsync(() => h.Db.Calls == 1, "backfill persisted");

        h.Time.Advance(TimeSpan.FromHours(1));
        var closed = Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 10), 123m);
        await h.Streams.PushCandleAsync(EthFutures, CandleInterval.OneHour, closed);
        await h.Streams.PushCandleAsync(EthFutures, CandleInterval.OneHour, closed with { Close = 999m });

        Assert.Equal(31, series.Count);
        Assert.True(series.IsReady);
        var snapshot = await h.Feed.LoadAsync(agent, Bare, EthFutures, closed, Ct);
        Assert.Equal(30, snapshot.Candles.Count);
        Assert.Equal(123m, snapshot.Latest!.Close);
        Assert.Equal(At(6, 5), snapshot.Candles[0].OpenTime);
        await Wait.UntilAsync(() => h.Db.Calls == 2, "streamed candle persisted once");
        Assert.Single(h.Exchange.RecentCandleReads);
    }

    [Fact]
    public async Task AHoleTheStreamLeftIsRepairedInTheBackgroundAndTheHandlerNeverWaits()
    {
        await using var h = new Harness();
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, null, series: 1);
        var series = h.Feed.Find(EthFutures, CandleInterval.OneHour)!;

        h.Time.Advance(TimeSpan.FromHours(4));
        await h.Streams.PushCandleAsync(EthFutures, CandleInterval.OneHour, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 13)));

        await Wait.UntilAsync(() => series.IsReady && series.Count == 34, "repaired");
        var read = Assert.Single(h.Exchange.CandleReads);
        Assert.Equal((At(7, 10), At(7, 12)), (read.From, read.To));
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("is missing 3 candle(s) from 2026-01-07T10:00:00", StringComparison.Ordinal));
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("repaired: 3 of 3 missing candle(s)", StringComparison.Ordinal));

        var snapshot = await h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 13)), Ct);
        Assert.Equal(30, snapshot.Candles.Count);
        AssertContiguous(snapshot.Candles, CandleInterval.OneHour);
        Assert.Equal(At(7, 13), snapshot.Latest!.OpenTime);
        Assert.Single(h.Exchange.CandleReads);
    }

    [Fact]
    public async Task AHoleARangedReadDoesNotFillIsRefilledByAFreshBackfillAndOneTheExchangeLacksIsAcceptedShort()
    {
        await using var h = new Harness();
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, null, series: 1);
        var series = h.Feed.Find(EthFutures, CandleInterval.OneHour)!;
        h.Exchange.OnCandles = (_, _, _, _, _) => [];

        // The ranged read answers nothing: the candles before the hole are dropped and a fresh backfill refills the depth.
        h.Time.Advance(TimeSpan.FromHours(4));
        await h.Streams.PushCandleAsync(EthFutures, CandleInterval.OneHour, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 13)));
        await Wait.UntilAsync(() => series.IsReady && series.Count == 30, "refilled");
        Assert.Single(h.Exchange.CandleReads);
        Assert.Equal(2, h.Exchange.RecentCandleReads.Count);
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("dropped 30 candle(s) before a hole", StringComparison.Ordinal));
        var snapshot = await h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 13)), Ct);
        Assert.Equal(30, snapshot.Candles.Count);
        AssertContiguous(snapshot.Candles, CandleInterval.OneHour);
        Assert.False(series.Exhausted);

        // The exchange's own history lacks 15:00: the series is accepted as the contiguous tail and evaluated as it is.
        h.Exchange.OnRecentCandles = (symbol, interval, count) => Harness.Recent(symbol, interval, count, h.Time.GetUtcNow()).Where(c => c.OpenTime != At(7, 15)).ToList();
        h.Time.Advance(TimeSpan.FromHours(3));
        await h.Streams.PushCandleAsync(EthFutures, CandleInterval.OneHour, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 16)));
        await Wait.UntilAsync(() => series.IsReady, "accepted");
        Assert.True(series.Exhausted);
        Assert.Equal(1, series.Count);
        var shortWindow = await h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 16)), Ct);
        Assert.Equal([At(7, 16)], shortWindow.Candles.Select(c => c.OpenTime));
        Assert.Equal(3, h.Exchange.RecentCandleReads.Count);
        Assert.DoesNotContain(h.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task AnEvaluationOfAnOlderCandleSeesTheSameWindowUntilTheSeriesDroppedIt()
    {
        await using var h = new Harness();
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, null, series: 1);
        var series = h.Feed.Find(EthFutures, CandleInterval.OneHour)!;
        Assert.Equal(38, series.Capacity);
        var first = series.First!.OpenTime;
        var closed = Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9));
        var before = await h.Feed.LoadAsync(agent, Bare, EthFutures, closed, Ct);

        for (var i = 1; i <= 4; i++)
            await h.Streams.PushCandleAsync(EthFutures, CandleInterval.OneHour, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9 + i)));
        var after = await h.Feed.LoadAsync(agent, Bare, EthFutures, closed, Ct);

        Assert.Equal(before.Candles.Select(c => c.OpenTime), after.Candles.Select(c => c.OpenTime));
        Assert.Equal(34, series.Count);

        for (var i = 5; i <= 9; i++)
            await h.Streams.PushCandleAsync(EthFutures, CandleInterval.OneHour, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9 + i)));
        Assert.Equal(38, series.Count);
        Assert.Equal(first.AddHours(1), series.First!.OpenTime);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, first), Ct));
        Assert.Contains("older than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHigherTimeframeCandleClosingAtTheCutoffIsReadWhenTheStreamIsBehindAndExcludedBeforeIt()
    {
        await using var h = new Harness(start: At(6, 23, 0).AddSeconds(5));
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, "trend-pullback", series: 2);
        Assert.Equal(At(5, 0), h.Feed.Find(EthFutures, CandleInterval.OneDay)!.Last!.OpenTime);

        // Midnight: the 23:00 candle and the day's candle close at the same instant; the daily stream has not delivered yet.
        h.Time.Advance(TimeSpan.FromHours(1));
        var snapshot = await h.Feed.LoadAsync(agent, h.Strategy("trend-pullback"), EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(6, 23)), Ct);

        var daily = snapshot.Higher(CandleInterval.OneDay);
        Assert.Equal(At(6, 0), daily[^1].OpenTime);
        Assert.Equal(25, daily.Count);
        var read = Assert.Single(h.Exchange.CandleReads);
        Assert.Equal(("ETHUSDT", CandleInterval.OneDay, At(6, 0), At(6, 0)), (read.Symbol, read.Interval, read.From, read.To));

        // An hour earlier the day had not closed: excluded, and nothing is read for it.
        var earlier = await h.Feed.LoadAsync(agent, h.Strategy("trend-pullback"), EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(6, 22)), Ct);
        Assert.Equal(At(5, 0), earlier.Higher(CandleInterval.OneDay)[^1].OpenTime);
        Assert.Equal(At(6, 22), earlier.Latest!.OpenTime);
        Assert.Single(h.Exchange.CandleReads);
    }

    [Fact]
    public async Task AnEvaluationWaitsForTheBackfillInFlightInsteadOfReadingAgain()
    {
        await using var h = new Harness();
        var gate = new ManualResetEventSlim(false);
        var attempts = 0;
        h.Exchange.OnRecentCandles = (symbol, interval, count) =>
        {
            Interlocked.Increment(ref attempts);
            gate.Wait(TimeSpan.FromSeconds(10));
            return Harness.Recent(symbol, interval, count, h.Time.GetUtcNow());
        };
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");

        // The faked exchange blocks the thread the backfill runs on, so tracking happens off the test thread.
        _ = Task.Run(() => h.Feed.Track(h.Needs(agent, EthFutures, null)), Ct);
        await Wait.UntilAsync(() => Volatile.Read(ref attempts) == 1, "backfill in flight");
        var load = h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9)), Ct);
        await Wait.SettleAsync(100);
        Assert.False(load.IsCompleted);

        gate.Set();
        var snapshot = await load;

        Assert.Equal(30, snapshot.Candles.Count);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task AFailedBackfillIsTriedAgainByTheEvaluationAndThenFailsIt()
    {
        await using var h = new Harness();
        h.Exchange.OnRecentCandles = (_, _, _) => throw new TimeoutException("binance down");
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");

        h.Feed.Track(h.Needs(agent, EthFutures, null));
        await Wait.UntilAsync(() => h.Logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be loaded; the next evaluation tries again", StringComparison.Ordinal)), "background failure logged");
        Assert.Equal(2, h.Exchange.RecentCandleReads.Count);

        await Assert.ThrowsAsync<TimeoutException>(() => h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9)), Ct));
        Assert.Equal(4, h.Exchange.RecentCandleReads.Count);

        h.Exchange.OnRecentCandles = (symbol, interval, count) => Harness.Recent(symbol, interval, count, h.Time.GetUtcNow());
        var snapshot = await h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9)), Ct);
        Assert.Equal(30, snapshot.Candles.Count);
        Assert.Equal(5, h.Exchange.RecentCandleReads.Count);
        Assert.DoesNotContain(h.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task TheMarkPriceComesFromTheStreamElseOverRestElseNotAtAll()
    {
        await using var h = new Harness();
        var rest = 0;
        h.Exchange.OnMarkPrice = symbol =>
        {
            rest++;
            return new MarkPrice(symbol, 100.5m, null, 0.0001m, Start.AddHours(8), Start);
        };
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, null, series: 1);
        var closed = Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9));

        var fromRest = await h.Feed.LoadAsync(agent, Bare, EthFutures, closed, Ct);
        Assert.Equal(0.0001m, fromRest.MarkPrice!.FundingRate);
        Assert.Equal(1, rest);

        await h.Streams.PushMarkPriceAsync(EthFutures, FakeMarketStreams.Mark("ETHUSDT", 101m, 0.0002m, Start));
        var streamed = await h.Feed.LoadAsync(agent, Bare, EthFutures, closed, Ct);
        Assert.Equal(0.0002m, streamed.MarkPrice!.FundingRate);
        Assert.Equal(Start.AddHours(8), streamed.MarkPrice.NextFundingTime);
        Assert.Equal(1, rest);

        await using var g = new Harness();
        g.Streams.MarkSubscribeError = new InvalidOperationException("no socket");
        var other = g.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await g.TrackAsync(other, EthFutures, null, series: 1);
        var without = await g.Feed.LoadAsync(other, Bare, EthFutures, closed, Ct);
        Assert.Null(without.MarkPrice);
        Assert.Contains(g.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be opened", StringComparison.Ordinal));
        Assert.Contains(g.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read", StringComparison.Ordinal));
        Assert.DoesNotContain(g.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task ASeriesTheStrategyNeedsButTrackingDidNotKnowIsLoadedOnDemand()
    {
        await using var h = new Harness();
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, null, series: 1);

        var snapshot = await h.Feed.LoadAsync(agent, h.Strategy("trend-pullback"), EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9)), Ct);

        Assert.Equal(25, snapshot.Higher(CandleInterval.OneDay).Count);
        Assert.Equal(2, h.Exchange.RecentCandleReads.Count);
        Assert.True(h.Streams.HasCandleSubscription(EthFutures, CandleInterval.OneDay));
        h.Feed.Untrack(agent.Id);
        Assert.Equal(0, h.Streams.CandleSubscriptions);
    }

    [Fact]
    public async Task AReferenceFailureIsToleratedAndAHigherTimeframeFailureFailsTheEvaluation()
    {
        await using var h = new Harness();
        h.Exchange.OnRecentCandles = (symbol, interval, count) => symbol == "BTCUSDT" ? throw new TimeoutException("binance down") : Harness.Recent(symbol, interval, count, h.Time.GetUtcNow());
        var eth = h.Agent(EthTestnet, CandleInterval.FourHours, "swing-pullback");
        h.Feed.Track(h.Needs(eth, EthTestnet, "swing-pullback"));
        await Wait.UntilAsync(() => h.Logs.Entries.Any(e => e.Message.Contains("could not be loaded; the next evaluation tries again", StringComparison.Ordinal)), "reference failed");

        var snapshot = await h.Feed.LoadAsync(eth, h.Strategy("swing-pullback"), EthTestnet, Harness.Candle("ETHUSDT", CandleInterval.FourHours, At(7, 4)), Ct);

        Assert.Equal(20, snapshot.Candles.Count);
        Assert.Equal(25, snapshot.Higher(CandleInterval.OneDay).Count);
        Assert.Equal(20, snapshot.Higher(CandleInterval.OneWeek).Count);
        Assert.Empty(snapshot.Reference(BtcWeekly));
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Reference series", StringComparison.Ordinal) && e.Message.Contains("evaluating with the 0 candle(s) it holds", StringComparison.Ordinal));

        await using var g = new Harness();
        g.Exchange.OnRecentCandles = (symbol, interval, count) => interval == CandleInterval.OneDay ? throw new TimeoutException("binance down") : Harness.Recent(symbol, interval, count, g.Time.GetUtcNow());
        var agent = g.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        g.Feed.Track(g.Needs(agent, EthFutures, "trend-pullback"));
        await Wait.UntilAsync(() => g.Logs.Entries.Any(e => e.Message.Contains("could not be loaded; the next evaluation tries again", StringComparison.Ordinal)), "daily failed");

        await Assert.ThrowsAsync<TimeoutException>(() => g.Feed.LoadAsync(agent, g.Strategy("trend-pullback"), EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9)), Ct));
    }

    [Fact]
    public async Task DisposeReleasesEverySeriesAndLease()
    {
        await using var h = new Harness();
        var agent = h.Agent(EthFutures, CandleInterval.OneHour, "trend-pullback");
        await h.TrackAsync(agent, EthFutures, "trend-pullback", series: 2);

        h.Feed.Dispose();

        Assert.Equal(0, h.Streams.CandleSubscriptions);
        Assert.Equal(0, h.Streams.MarkSubscriptions);
        Assert.Empty(h.Feed.Series);
        h.Feed.Track(h.Needs(agent, EthFutures, "trend-pullback"));
        Assert.Equal(0, h.Streams.CandleSubscriptions);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Feed.LoadAsync(agent, Bare, EthFutures, Harness.Candle("ETHUSDT", CandleInterval.OneHour, At(7, 9)), Ct));
    }

    private static void AssertContiguous(IReadOnlyList<Candle> candles, CandleInterval interval)
    {
        for (var i = 1; i < candles.Count; i++)
            Assert.Equal(candles[i - 1].OpenTime + interval.ToTimeSpan(), candles[i].OpenTime);
    }

    /// <summary>The module over fakes with small depths (15m 40, 1H 30, 4H 20, 1D 25, 1W 20) and a database that is down.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(DateTimeOffset? start = null)
        {
            Time = new FakeTimeProvider(start ?? Start);
            Exchange = new FakeExchangeClientFactory { OnRecentCandles = (symbol, interval, count) => Recent(symbol, interval, count, Time.GetUtcNow()) };
            var config = new Dictionary<string, string?>
            {
                ["MarketData:BackfillCandles:15m"] = "40",
                ["MarketData:BackfillCandles:1H"] = "30",
                ["MarketData:BackfillCandles:4H"] = "20",
                ["MarketData:BackfillCandles:1D"] = "25",
                ["MarketData:BackfillCandles:1W"] = "20",
            };
            Provider = MarketDataTestHost.BuildServices(logs: Logs, time: Time, exchange: Exchange, streams: Streams, config: config, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(Db));
            Feed = Provider.GetRequiredService<CandleFeed>();
            Catalog = Provider.GetRequiredService<IStrategyCatalog>();
        }

        public FakeExchangeClientFactory Exchange { get; }

        public FakeMarketStreams Streams { get; } = new();

        public FakeTimeProvider Time { get; }

        public TestLoggerProvider Logs { get; } = new();

        public ThrowingDbContextFactory Db { get; } = new();

        public ServiceProvider Provider { get; }

        public CandleFeed Feed { get; }

        public IStrategyCatalog Catalog { get; }

        public StrategyDescriptor Strategy(string id) => Catalog.Find(id)!;

        public Agent Agent(MarketKey key, CandleInterval timeframe, string strategyId) =>
            new() { Id = Guid.NewGuid(), Name = "x", MarketType = key.Market, Symbol = key.Symbol, Timeframe = timeframe, StrategyId = strategyId, StrategyVersion = 1 };

        public MarketDataNeeds Needs(Agent agent, MarketKey key, string? strategyId) => new(agent.Id, key, agent.Timeframe, strategyId is null ? null : Strategy(strategyId));

        /// <summary>Tracks and waits until the feed holds <paramref name="series"/> ready series.</summary>
        public async Task TrackAsync(Agent agent, MarketKey key, string? strategyId, int series)
        {
            Feed.Track(Needs(agent, key, strategyId));
            await Wait.UntilAsync(() => Feed.Series.Count == series && Feed.Series.All(s => s.IsReady), $"{series} series ready");
        }

        /// <summary>The newest <paramref name="count"/> closed candles before <paramref name="now"/>, as the exchange answers.</summary>
        public static IReadOnlyList<Candle> Recent(string symbol, CandleInterval interval, int count, DateTimeOffset now)
        {
            var lastOpen = interval.OpenTimeOf(now) - interval.ToTimeSpan();
            return Enumerable.Range(0, count).Reverse().Select(i => Candle(symbol, interval, lastOpen - i * interval.ToTimeSpan())).ToList();
        }

        public static Candle Candle(string symbol, CandleInterval interval, DateTimeOffset open, decimal close = 100m) => FakeMarketStreams.ClosedCandle(symbol, interval, open, close);

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
