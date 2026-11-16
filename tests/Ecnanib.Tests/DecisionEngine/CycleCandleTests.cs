using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// A cycle starts when a candle of the agent's timeframe closes, and only closed candles are
/// used (FR-CYC-1). Which candle is closed is the exchange's word: the cycles of a close start
/// with candles of which the newest closed at that close, and wait until the exchange gives
/// them. See "The candle of the close" in docs/cycles.md.
/// </summary>
public class CycleCandleTests
{
    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_cycle_waits_until_the_exchange_has_closed_its_candle()
    {
        using var application = new CyclesApplication(Quarter);
        var agent = application.Agents!.Give(Guid.NewGuid());

        // Just after the close the exchange gives no answer once, and then has not begun the
        // next candle: the candle that closed is still in progress to it, so the candles come
        // one short. The third answer has it.
        var asks = 0;
        application.Candles.Answer = asked => Interlocked.Increment(ref asks) switch
        {
            1 => StandInCandles.NoAnswer,
            2 => StandInCandles.Through(Quarter.AddMinutes(-15), asked.Interval),
            _ => null,
        };

        await application.RunAsync(Quarter).Within();

        // The cycle started with the candle of its close, and not with the ones before it.
        var cycle = Assert.Single(application.Cycles.Started);
        Assert.Equal(agent, cycle.Agent.Id);
        Assert.Equal(Quarter, cycle.ClosedAt);
        Assert.Equal(Quarter, cycle.Candles.Candles[^1].ClosedAt);
        Assert.Equal(ExchangeCallOutcome.Succeeded, cycle.Candles.Outcome);

        // Asked again until it was there, for the candles of the agent's own timeframe.
        Assert.Equal(
            Enumerable.Repeat(new AskedCandles(Market.Spot, TradingMode.Demo, "BTCUSDT", CandleInterval.FifteenMinutes), 3),
            application.Candles.Asked);
        Assert.DoesNotContain(application.Written(), written => written.Level is "Warning" or "Error" or "Fatal");
    }

    [Fact]
    public async Task The_candles_of_a_symbol_and_timeframe_are_asked_for_once_however_many_agents_share_them()
    {
        using var application = new CyclesApplication(Quarter);
        var agents = application.Agents!;

        // Three users with an agent on the same symbol and timeframe, on one market in one
        // environment, and two agents that share only a part of that.
        Guid[] sharing = [agents.Give(Guid.NewGuid()), agents.Give(Guid.NewGuid()), agents.Give(Guid.NewGuid())];
        var otherSymbol = agents.Give(Guid.NewGuid(), symbol: "SOLUSDT");
        var otherMarket = agents.Give(Guid.NewGuid(), market: Market.Futures);

        await application.RunAsync(Quarter).Within();

        // One ask for each symbol and timeframe on a market in an environment (FR-BIN-3).
        Assert.Equal(
            new[]
            {
                new AskedCandles(Market.Futures, TradingMode.Demo, "BTCUSDT", CandleInterval.FifteenMinutes),
                new AskedCandles(Market.Spot, TradingMode.Demo, "BTCUSDT", CandleInterval.FifteenMinutes),
                new AskedCandles(Market.Spot, TradingMode.Demo, "SOLUSDT", CandleInterval.FifteenMinutes),
            },
            application.Candles.Asked.OrderBy(asked => asked.Market.ToString()).ThenBy(asked => asked.Symbol));

        // The agents that share them decide on the same candles.
        var started = application.Cycles.Started.ToDictionary(cycle => cycle.Agent.Id);
        Assert.Equal(5, started.Count);
        Assert.All(sharing, agent => Assert.Same(started[sharing[0]].Candles, started[agent].Candles));
        Assert.NotSame(started[sharing[0]].Candles, started[otherSymbol].Candles);
        Assert.NotSame(started[sharing[0]].Candles, started[otherMarket].Candles);
    }

    [Fact]
    public async Task Cycles_whose_candle_is_not_closed_in_time_are_skipped_and_the_others_run()
    {
        using var application = new CyclesApplication(Quarter);
        var agents = application.Agents!;

        var withCandle = agents.Give(Guid.NewGuid());
        agents.Give(Guid.NewGuid(), symbol: "SOLUSDT");

        // The exchange does not begin the next candle of one symbol: its market is halted.
        application.Candles.Answer = asked =>
            asked.Symbol == "SOLUSDT" ? StandInCandles.Through(Quarter.AddMinutes(-15), asked.Interval) : null;

        var run = application.RunAsync(Quarter);

        // The cycles that have their candles wait a moment for the others, so that all find
        // their places in the order, and then start without them.
        application.Clock.Set(Quarter.AddSeconds(5));
        await application.Cycles.StartedAsync(1);

        // Half a minute after the close the candle is not waited for any longer.
        application.Clock.Set(Quarter.AddSeconds(30));
        await run.Within();

        Assert.Equal([withCandle], application.Cycles.StartedFor);

        var skipped = Assert.Single(application.Written(), written => written.Level == "Warning");
        Assert.Equal(CyclesLog.NoCandle, skipped.Template);
        Assert.Equal(("SOLUSDT", "Spot", "Demo", "15m"), (skipped.Text("Symbol"), skipped.Text("Market"), skipped.Text("TradingMode"), skipped.Text("Timeframe")));
        Assert.Equal(1, skipped["CycleCount"].GetInt32());
        Assert.Equal(Quarter, skipped["ClosedAt"].GetDateTimeOffset());

        // The exchange answered: what was missing is the candle, and that is said.
        Assert.Equal("Succeeded", skipped.Text("ExchangeCallOutcome"));

        var summary = application.SummaryOf(Quarter);
        Assert.Equal((1, 1), (summary["Ended"].GetInt32(), summary["WithoutCandle"].GetInt32()));
    }

    [Fact]
    public async Task Behind_the_gateway_a_cycle_is_handed_the_candles_through_its_close_and_never_the_one_in_progress()
    {
        // The clock of the service is a quarter of a second past the close. Binance's clock is
        // behind it at first, so that the candle of the close is still in progress there.
        var clock = new TestClock(Quarter.AddMilliseconds(250));
        var binanceShows = Quarter.AddMilliseconds(-1);

        var binance = new StandInSystem(request => StandInSystem.Json(
            HttpStatusCode.OK, BinanceCalls.CandlesAt(binanceShows, BinanceCalls.IntervalOf(request))));
        var cycles = new StandInCycles();
        var agents = new StandInRunningAgents();

        using var application = binance.Behind(
            ExchangeGatewayModule.HttpClientName,
            services: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IDecisionCycle>(cycles);
                services.AddSingleton<IRunningAgents>(agents);
            },
            runsCycles: true);

        agents.Give(Guid.NewGuid());

        var run = application.Services.GetServices<IBackgroundJob>()
            .Single(job => job.Name == "decision-cycles")
            .RunAsync(new BackgroundJobRun(Quarter), TestContext.Current.CancellationToken);

        // The first answer of Binance has the candle of the close as its newest row, which is
        // the one in progress: the gateway does not give it, and no cycle starts with it.
        await Wait.UntilAsync(() => binance.Calls.Count >= 1, "the candles to be asked for");
        Assert.Empty(cycles.Started);

        // Binance begins the next candle, and the next ask finds the candle of the close closed.
        binanceShows = Quarter.AddMilliseconds(300);
        await run.Within();

        var cycle = Assert.Single(cycles.Started);
        Assert.Equal(Quarter, cycle.Candles.Candles[^1].ClosedAt);
        Assert.Equal(BinanceCalls.ClosedAt(Quarter.AddMilliseconds(300), CandleInterval.FifteenMinutes), cycle.Candles.Candles);

        // Every candle that the cycle was handed had closed by its close: none is in progress.
        Assert.All(cycle.Candles.Candles, candle => Assert.True(candle.ClosedAt <= Quarter));

        // The candles of the Spot market in the test environment, which is where the agent is.
        Assert.All(binance.Calls, call => Assert.Equal("/api/v3/klines", call.Address.AbsolutePath));
        Assert.True(binance.Calls.Count >= 2, "The candles were asked for again after they came one short.");
    }
}
