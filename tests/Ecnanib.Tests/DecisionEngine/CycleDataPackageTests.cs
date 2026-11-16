using System.Net;
using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// A cycle that starts builds its data package (FR-CYC-2, SRS 3.2, step 2), and makes no decision
/// yet: the calls of the team are not built. A cycle whose market data the exchange does not
/// give makes none either, and the other cycles go on. See "The data package" in docs/cycles.md.
/// </summary>
public class CycleDataPackageTests
{
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_cycle_that_starts_builds_its_data_package()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agents = application.Agents!;
        var market = application.Market!;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        var onSpot = agents.Give(ada);
        var onFutures = agents.Give(grace, market: Market.Futures);

        await application.RunAsync(Quarter).Within();

        // What starts the cycles asked for the candles of the close, and each cycle for the
        // candles of the two higher timeframes of its agent, on the market of the agent.
        Assert.Equal(
            from asked in new[] { Market.Futures, Market.Spot }
            from interval in new[] { CandleInterval.FifteenMinutes, CandleInterval.OneHour, CandleInterval.FourHours }
            select new AskedCandles(asked, TradingMode.Demo, "BTCUSDT", interval),
            market.Asked.OrderBy(asked => asked.Market.ToString()).ThenBy(asked => asked.Interval));

        // The mark price and the open interest, for the agent on Futures alone.
        Assert.Equal(
            [new AskedOfFutures("mark price", TradingMode.Demo, "BTCUSDT"), new AskedOfFutures("open interest", TradingMode.Demo, "BTCUSDT")],
            market.AskedFutures.OrderBy(asked => asked.What));

        // A package for each cycle, said once, with the user, the agent, and the cycle.
        var built = application.Written(DataPackagesLog.Area, DataPackagesLog.Built);

        Assert.Equal(
            new[] { (ada, onSpot), (grace, onFutures) }.Select(of => (of.Item1.ToString(), of.Item2.ToString())).Order(),
            built.Select(written => (written.Text("UserId")!, written.Text("AgentId")!)).Order());
        Assert.All(built, written =>
        {
            Assert.Equal("Information", written.Level);
            Assert.True(written.Has("CycleId"));
            Assert.Equal(Quarter, written["ClosedAt"].GetDateTimeOffset());

            // As many candles as the file of the tests says, of the 150 that the exchange gave.
            Assert.Equal((120, "15m"), (written["CandleCount"].GetInt32(), written.Text("Timeframe")));
            Assert.Equal((60, "1H, 4H"), (written["ContextCandleCount"].GetInt32(), written.Text("ContextTimeframes")));
            Assert.Equal(0, written["OrderCount"].GetInt32());
        });

        // The cycles ended, with no decision: nothing calls the team yet.
        var summary = application.SummaryOf(Quarter);

        Assert.Equal((2, 0), (summary["Ended"].GetInt32(), summary["Failed"].GetInt32()));
        Assert.DoesNotContain(
            application.Written().Concat(application.Written(DataPackagesLog.Area)),
            written => written.Level is "Warning" or "Error" or "Fatal");
    }

    [Fact]
    public async Task A_cycle_whose_market_data_the_exchange_does_not_give_makes_no_decision_and_the_others_go_on()
    {
        using var application = new CyclesApplication(Quarter, buildsPackages: true);
        var agents = application.Agents!;
        var market = application.Market!;

        var withData = agents.Give(Guid.NewGuid());
        var without = agents.Give(Guid.NewGuid(), symbol: "SOLUSDT");

        // The limit on requests has no room for the candles of an hour of one symbol.
        market.Candles = asked => asked is { Symbol: "SOLUSDT", Interval: CandleInterval.OneHour }
            ? StandInMarket.NoCandles(ExchangeCallOutcome.RateLimited)
            : null;

        await application.RunAsync(Quarter).Within();

        // The cycle of that symbol failed, with no decision, and says which part was missing.
        var failed = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.Failed));

        Assert.Equal("Error", failed.Level);
        Assert.Equal(without.ToString(), failed.Text("AgentId"));
        Assert.Contains(
            "No data package is built: the exchange did not give the 1H candles (RateLimited). The cycle makes no decision.",
            failed.Exception,
            StringComparison.Ordinal);

        // The cycle of the other symbol has its package.
        var built = Assert.Single(application.Written(DataPackagesLog.Area, DataPackagesLog.Built));

        Assert.Equal(withData.ToString(), built.Text("AgentId"));

        var summary = application.SummaryOf(Quarter);

        Assert.Equal((1, 1), (summary["Ended"].GetInt32(), summary["Failed"].GetInt32()));

        // Nothing is asked again for that close. The next close starts the next cycles, and
        // the exchange answers by then.
        Assert.Single(market.Asked, asked => asked is { Symbol: "SOLUSDT", Interval: CandleInterval.OneHour });

        market.Candles = null;
        application.Clock.Set(Quarter.AddMinutes(15));

        await application.RunAsync(Quarter.AddMinutes(15)).Within();

        Assert.Equal(3, application.Written(DataPackagesLog.Area, DataPackagesLog.Built).Count);
        Assert.Single(application.Written(CyclesLog.Area, CyclesLog.Failed));
    }

    [Fact]
    public async Task Behind_the_gateway_the_cycles_of_a_symbol_share_every_read_of_their_data_packages()
    {
        // The clock of the service is a quarter of a second past the close, and Binance has
        // begun the next candle of every length.
        var clock = new TestClock(Quarter.AddMilliseconds(250));

        var binance = new StandInSystem(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            return StandInSystem.Json(
                HttpStatusCode.OK,
                path.EndsWith("/klines", StringComparison.Ordinal)
                    ? BinanceCalls.CandlesAt(clock.GetUtcNow(), BinanceCalls.IntervalOf(request), closed: 40)
                    : BinanceCalls.AnswerTo(path));
        });
        var agents = new StandInRunningAgents();

        using var application = binance.Behind(
            ExchangeGatewayModule.HttpClientName,
            services: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IRunningAgents>(agents);
            },
            runsCycles: true);

        // Three users with an agent on the same symbol and timeframe of Futures.
        Guid[] sharing =
        [
            agents.Give(Guid.NewGuid(), market: Market.Futures),
            agents.Give(Guid.NewGuid(), market: Market.Futures),
            agents.Give(Guid.NewGuid(), market: Market.Futures),
        ];

        await application.Services.GetServices<IBackgroundJob>()
            .Single(job => job.Name == "decision-cycles")
            .RunAsync(new BackgroundJobRun(Quarter), TestContext.Current.CancellationToken)
            .Within();

        // Every cycle built its package, from what Binance answered: 40 closed candles of each length.
        var built = application.Events()
            .Where(written => written.Area == DataPackagesLog.Area && written.Template == DataPackagesLog.Built)
            .ToList();

        Assert.Equal(sharing.Select(agent => agent.ToString()).Order(), built.Select(written => written.Text("AgentId")).Order());
        Assert.All(built, written =>
        {
            Assert.Equal((40, "15m"), (written["CandleCount"].GetInt32(), written.Text("Timeframe")));
            Assert.Equal((60, "1H, 4H"), (written["ContextCandleCount"].GetInt32(), written.Text("ContextTimeframes")));
        });

        // One read of each part for all three (FR-BIN-3): the candles of the close, those of the
        // two higher timeframes, the mark price with its funding, and the open interest, each on
        // the Futures market of the test environment.
        Assert.Equal(
            ["/fapi/v1/klines 15m", "/fapi/v1/klines 1h", "/fapi/v1/klines 4h", "/fapi/v1/openInterest", "/fapi/v1/premiumIndex"],
            binance.Calls
                .Select(call => call.Address.AbsolutePath
                    + string.Concat(call.Query.Where(parameter => parameter.Name == "interval").Select(parameter => $" {parameter.Value}")))
                .Order(StringComparer.Ordinal));
        Assert.All(binance.Calls, call => Assert.Equal(BinanceCalls.HostOf(Market.Futures, TradingMode.Demo), call.Address.Host));
    }
}
