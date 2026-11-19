using System.Globalization;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using static Ecnanib.Tests.DecisionEngine.ComposedPrompts;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The data package of a cycle contains the most recent closed candles of the agent's timeframe,
/// the higher-timeframe context of SRS 4.6, indicators computed by the system, the current
/// position, orders, and equity, the agent's limits, and on Futures the mark price, the funding,
/// and the open interest (FR-CYC-2). See "The data package" in docs/cycles.md.
/// </summary>
public class DataPackageTests
{
    /// <summary>A Monday at midnight in UTC: a candle of every length closes then, the week included.</summary>
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A quarter hour in the middle of a week, which closes a candle of 15 minutes and of no other length.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    /// <summary>The higher-timeframe context of each timeframe: the table of the timeframe modules of the specification (SRS 4.6).</summary>
    public static TheoryData<Timeframe, CandleInterval[]> Contexts => new()
    {
        { Timeframe.FifteenMinutes, [CandleInterval.OneHour, CandleInterval.FourHours] },
        { Timeframe.OneHour, [CandleInterval.FourHours, CandleInterval.OneDay] },
        { Timeframe.FourHours, [CandleInterval.OneDay, CandleInterval.OneWeek] },
        { Timeframe.OneDay, [CandleInterval.OneWeek] },
    };

    [Theory]

    // The default of the specification, of the 498 that one read of the exchange gives.
    [InlineData(200, 498, 200)]

    // What the file of the tests says.
    [InlineData(null, 150, 120)]

    // All that the exchange has, when those are fewer.
    [InlineData(200, 150, 150)]
    public async Task The_package_holds_the_most_recent_closed_candles_of_the_agents_timeframe(int? setting, int given, int held)
    {
        using var application = new PackagesApplication(Quarter, candles: setting);
        var agent = PackageAgents.On(Market.Spot, Timeframe.FifteenMinutes);
        var cycle = PackagesApplication.CycleOf(agent, Quarter, own: given);

        var package = await application.Application.DataPackages.BuildAsync(cycle, TestContext.Current.CancellationToken);

        // The newest of the candles that the cycle was handed, oldest first, through its close.
        Assert.Equal(Quarter, package.ClosedAt);
        Assert.Equal(CandleInterval.FifteenMinutes, package.Candles.Interval);
        Assert.Equal(held, package.Candles.Candles.Count);
        Assert.Equal(cycle.Candles.Candles.Skip(given - held), package.Candles.Candles);
        Assert.Equal(Quarter, package.Candles.Candles[^1].ClosedAt);

        // They are the ones that what starts the cycles read: they are not asked for again.
        Assert.DoesNotContain(application.Market.Asked, asked => asked.Interval == CandleInterval.FifteenMinutes);
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task The_package_holds_the_higher_timeframe_context_of_the_agents_timeframe(Timeframe timeframe, CandleInterval[] context)
    {
        using var application = new PackagesApplication(Monday);
        var agent = PackageAgents.On(Market.Futures, timeframe, TradingMode.Live, symbol: "SOLUSDT");

        var package = await application.BuildAsync(agent, Monday);

        // The higher timeframes of the table, shortest first, and no other.
        Assert.Equal(context, package.Context.Select(series => series.Interval));
        Assert.Equal(context, TimeframeContext.Of(timeframe));

        // Asked for once each, on the market of the agent, in its environment, for its symbol.
        Assert.Equal(
            context.Select(interval => new AskedCandles(Market.Futures, TradingMode.Live, "SOLUSDT", interval)),
            application.Market.Asked);

        Assert.All(package.Context, series =>
        {
            var given = StandInMarket.Through(Monday, series.Interval).Candles;

            // As many as the file says, the newest, oldest first. A candle of every length
            // closes on a Monday at midnight, so each series reaches through the close.
            Assert.Equal(30, series.Candles.Count);
            Assert.Equal(given.Skip(given.Count - 30), series.Candles);
            Assert.Equal(Monday, series.Candles[^1].ClosedAt);
        });
    }

    [Fact]
    public async Task The_context_is_what_had_closed_by_the_close_of_the_cycle_and_nothing_later()
    {
        using var application = new PackagesApplication(Quarter, contextCandles: 5);
        var agent = PackageAgents.On(Market.Spot, Timeframe.FifteenMinutes);

        // In the middle of a week, at 10:15, the latest hour closed at 10:00, and the latest
        // candle of four hours at 08:00.
        var package = await application.BuildAsync(agent, Quarter);

        DateTimeOffset[] through = [new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero)];

        Assert.Equal(through, package.Context.Select(series => series.Candles[^1].ClosedAt));
        Assert.Equal([5, 5], package.Context.Select(series => series.Candles.Count));
        Assert.Equal([StandInMarket.Given, StandInMarket.Given], package.Context.Select(series => series.ComputedFrom));

        // The turn of a cycle may come late. Two hours after the close the exchange has closed
        // two hours more, and one more candle of four hours: none of them is in the package of
        // that close, and its indicators are of the candles through the close alone.
        application.Clock.Set(Quarter.AddHours(2));

        var late = await application.BuildAsync(agent, Quarter);

        Assert.Equal(through, late.Context.Select(series => series.Candles[^1].ClosedAt));
        Assert.Equal([5, 5], late.Context.Select(series => series.Candles.Count));
        Assert.Equal([StandInMarket.Given - 2, StandInMarket.Given - 1], late.Context.Select(series => series.ComputedFrom));
        Assert.All(late.Context.Concat([late.Candles]), series =>
            Assert.All(series.Candles, candle => Assert.True(candle.ClosedAt <= Quarter)));

        Assert.All(late.Context, series => Assert.Equal(
            Indicators.Of([.. StandInMarket.Through(Quarter.AddHours(2), series.Interval).Candles.Where(candle => candle.ClosedAt <= Quarter)]),
            series.Indicators));
    }

    [Fact]
    public async Task The_indicators_of_every_series_are_computed_by_the_system_from_every_closed_candle_that_the_exchange_gave()
    {
        using var application = new PackagesApplication(Monday);
        application.Market.Candles = asked => StandInMarket.Through(Monday, asked.Interval, candles: 250);

        var agent = PackageAgents.On(Market.Spot, Timeframe.FourHours);
        var cycle = PackagesApplication.CycleOf(agent, Monday, own: 250);

        var package = await application.Application.DataPackages.BuildAsync(cycle, TestContext.Current.CancellationToken);

        // The candles of the agent's own timeframe: 120 of them are in the package, and the
        // indicators are of all 250, so the average of 200 candles is there as well.
        Assert.Equal(120, package.Candles.Candles.Count);
        Assert.Equal(250, package.Candles.ComputedFrom);
        Assert.Equal(Indicators.Of(cycle.Candles.Candles), package.Candles.Indicators);
        Assert.Null(Indicators.Of(package.Candles.Candles).Sma200);

        // The candles of each higher timeframe, in the same way.
        Assert.Equal(2, package.Context.Count);
        Assert.All(package.Context, series =>
        {
            Assert.Equal(30, series.Candles.Count);
            Assert.Equal(250, series.ComputedFrom);
            Assert.Equal(Indicators.Of(StandInMarket.Through(Monday, series.Interval, candles: 250).Candles), series.Indicators);
        });

        // Moving averages, relative strength, average true range, and volume averages: every
        // one of them, for every series.
        Assert.All(package.Context.Concat([package.Candles]), series => Assert.DoesNotContain(null, new[]
        {
            series.Indicators.Sma20, series.Indicators.Sma50, series.Indicators.Sma200, series.Indicators.Ema20,
            series.Indicators.Ema50, series.Indicators.Rsi14, series.Indicators.Atr14, series.Indicators.VolumeAverage20,
        }));
    }

    [Fact]
    public async Task The_indicators_of_a_package_have_the_known_values_of_its_candles()
    {
        using var application = new PackagesApplication(Monday);
        var agent = PackageAgents.On(Market.Spot, Timeframe.OneDay);

        // Twenty-one days: the close rises by 1 a day from 100 to 119, and then to 125. The high
        // is 1 above the close and the low 1 below it, and the volume is 3 every day.
        decimal[] closes = [.. Enumerable.Range(100, 20).Select(close => (decimal)close), 125m];
        var cycle = new CycleStart(
            Guid.CreateVersion7(),
            agent,
            Roles: [],
            Monday,
            new ExchangeCandles(
                ExchangeCallOutcome.Succeeded,
                [
                    .. closes.Select((close, day) => new ExchangeCandle(
                        Monday.AddDays(day - closes.Length), Monday.AddDays(day - closes.Length + 1), close - 0.5m, close + 1m, close - 1m, close, 3m)),
                ],
                Monday));

        var package = await application.Application.DataPackages.BuildAsync(cycle, TestContext.Current.CancellationToken);
        var indicators = package.Candles.Indicators;

        // The mean of the latest 20 closes: (101 + ... + 119 + 125) / 20 = 2215 / 20.
        Assert.Equal(110.75m, indicators.Sma20);

        // It starts as the mean of the first 20, which is 109.5, and moves by 2 / 21 of what
        // the last close differs by: 109.5 + 15.5 * 2 / 21 = 110.976190...
        Assert.Equal(110.97619m, Math.Round(indicators.Ema20!.Value, 5));

        // The close never fell.
        Assert.Equal(100m, indicators.Rsi14);

        // The true range is 2 on every day but the last, when the high is 7 above the close
        // before: (2 * 13 + 7) / 14 = 2.357142...
        Assert.Equal(2.3571429m, Math.Round(indicators.Atr14!.Value, 7));
        Assert.Equal(3m, indicators.VolumeAverage20);

        // Too few candles for the others.
        Assert.Null(indicators.Sma50);
        Assert.Null(indicators.Sma200);
        Assert.Null(indicators.Ema50);

        // A role reads the numbers as they are: worked out to eight significant digits, and
        // the relative strength to two decimal places.
        Assert.Contains(
            "Indicators at that close, computed by the system from 21 closed 1D candles:\n" +
            "- Moving averages of the close: SMA20 110.75, SMA50 not available, SMA200 not available, EMA20 110.97619, EMA50 not available\n" +
            "- Relative strength: RSI14 100\n" +
            "- Average true range: ATR14 2.3571429\n" +
            "- Volume: 3 in the latest candle, average of 20 candles 3\n",
            package.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_Futures_the_package_also_holds_the_mark_price_the_funding_and_the_open_interest_and_on_Spot_none_of_them()
    {
        using var application = new PackagesApplication(Quarter);
        var market = application.Market;

        foreach (var mode in Enum.GetValues<TradingMode>())
        {
            var onFutures = await application.BuildAsync(PackageAgents.On(Market.Futures, Timeframe.FifteenMinutes, mode), Quarter);

            // The mark price, the funding rate with the time of the next funding, and the open
            // interest (FR-BIN-F4), as the exchange says them in the environment of the agent.
            Assert.Equal(
                new FuturesData(StandInMarket.MarkPrice, StandInMarket.FundingRate, market.NextFundingAt, StandInMarket.OpenInterest),
                onFutures.Futures);
            Assert.Equal(
                [new AskedOfFutures("mark price", mode, "BTCUSDT"), new AskedOfFutures("open interest", mode, "BTCUSDT")],
                market.AskedFutures.TakeLast(2).OrderBy(asked => asked.What));
            Assert.EndsWith(
                "\n\nFutures: mark price 61888.1, funding rate 0.01%, next funding 2026-11-18T16:00Z, open interest 82450.5 BTC",
                onFutures.Text,
                StringComparison.Ordinal);
        }

        var asked = market.AskedFutures.Count;

        // An agent on Spot has neither: nothing of Futures is in its package, and nothing is asked for.
        var onSpot = await application.BuildAsync(PackageAgents.On(Market.Spot, Timeframe.FifteenMinutes), Quarter);

        Assert.Null(onSpot.Futures);
        Assert.Equal(asked, market.AskedFutures.Count);
        Assert.DoesNotContain("Futures", onSpot.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("funding", onSpot.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("open interest", onSpot.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_package_holds_the_position_the_orders_and_the_equity_of_the_agent_and_its_limits()
    {
        var position = new PositionContext(PositionSide.Long, EntryPrice: 61000m, Quantity: 0.015m, StopLoss: 59500m, TakeProfit: 64000m);
        OpenOrder[] orders =
        [
            new(OpenOrderKind.StopLoss, OrderSide.Sell, 0.015m, 59500m),
            new(OpenOrderKind.TakeProfit, OrderSide.Sell, 0.015m, 64000m),
        ];
        var holdings = new StandInHoldings(new AgentHoldings(Equity: 1040.5m, position, orders, EquityAtDayStart: 1040.5m, PeakEquity: 1040.5m));

        using var application = new PackagesApplication(Monday, holdings);
        var agent = PackageAgents.On(Market.Futures, Timeframe.FourHours);

        var package = await application.BuildAsync(agent, Monday);

        // What the agent holds, as whatever keeps positions and orders says it, asked once.
        Assert.Equal(1040.5m, package.Agent.Equity);
        Assert.Equal(position, package.Agent.Position);
        Assert.Equal(orders, package.Orders);
        Assert.Equal([agent.Id], holdings.Asked);

        // The limits of the agent: its own risk settings, as they are stored.
        Assert.Equal(
            new AgentRisk(
                RiskPerTradePercent: 1.5m,
                MinRewardToRisk: 1.75m,
                LeverageCap: 4,
                LiquidationBuffer: 2.5m,
                DailyLossPercent: 5m,
                DrawdownPercent: 20m,
                MaxEntryDistancePercent: 0.8m),
            package.Agent.Limits);

        // With what the agent trades, it is the context of the agent that a prompt states.
        Assert.Equal(AgentContext.Of(agent, 1040.5m, position), package.Agent);
        Assert.Equal(new PromptParts(package.Agent, package.Text, ["A lesson."]), package.PartsWith(["A lesson."]), PartsAreTheSame);

        // The orders are in what a role reads. The equity, the position, and the limits are
        // not repeated there: the prompt states them as the context of the agent.
        Assert.Contains(
            "\n\nOpen orders:\n- stop-loss: sell 0.015 at 59500\n- take-profit: sell 0.015 at 64000\n", package.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("1040.5", package.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_nothing_keeps_positions_and_orders_an_agent_holds_none_and_its_equity_is_its_allocated_capital()
    {
        // An agent that has no rows: it has placed nothing, so it holds nothing and has made
        // and lost nothing.
        using var application = new PackagesApplication(Monday);
        var agent = PackageAgents.On(Market.Spot, Timeframe.OneDay, allocatedCapital: 2500m);

        var package = await application.BuildAsync(agent, Monday);

        // The capital that its user allocated to it, with no profit and no loss to date.
        Assert.Equal(2500m, package.Agent.Equity);
        Assert.Null(package.Agent.Position);
        Assert.Empty(package.Orders);
        Assert.EndsWith("\n\nOpen orders: none", package.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Once_positions_or_orders_are_stored_what_keeps_them_says_what_an_agent_holds_in_its_data_package()
    {
        using var application = new EcnanibApplicationFactory();
        using var model = ModelOnly.Context();

        // Where nothing keeps positions and orders, a package says that the agent holds nothing,
        // and that its equity is its allocated capital. As soon as a table holds them, a package
        // that still said so would have the team decide as if the agent had no position, while
        // it has one. They are kept now, and what keeps them answers: with the position, the
        // orders, and the equity of the agent, which is its capital plus its result (FR-RSK-8).
        var stored = AgentsRules.TablesOfPositionsOrOrders(model.Model);
        var answers = application.Services.GetServices<IAgentHoldings>().Any();

        Assert.True(
            answers || stored.Count == 0,
            $"Positions or orders are stored ({string.Join(", ", stored)}), and nothing says what an agent holds as its cycle " +
            "starts. The data package contains the current position, orders, and equity of the agent (FR-CYC-2). Implement " +
            $"{nameof(IAgentHoldings)} in the component that keeps them, and register it. See docs/cycles.md.");
    }

    [Fact]
    public async Task A_read_that_does_not_succeed_gives_no_package()
    {
        using var application = new PackagesApplication(Quarter);
        var market = application.Market;
        var agent = PackageAgents.On(Market.Futures, Timeframe.FifteenMinutes);

        (string Read, string Missing)[] reads =
        [
            ("1H", "the 1H candles"),
            ("4H", "the 4H candles"),
            ("mark price", "the mark price and the funding"),
            ("open interest", "the open interest"),
        ];

        // Every part of the market data, and every way a read ends without it.
        foreach (var (read, missing) in reads)
        {
            foreach (var outcome in Enum.GetValues<ExchangeCallOutcome>().Where(outcome => outcome != ExchangeCallOutcome.Succeeded))
            {
                market.Candles = asked => TimeframeContext.Code(asked.Interval) == read ? StandInMarket.NoCandles(outcome) : null;
                market.MarkPriceOutcome = read == "mark price" ? outcome : ExchangeCallOutcome.Succeeded;
                market.OpenInterestOutcome = read == "open interest" ? outcome : ExchangeCallOutcome.Succeeded;

                var refused = await Assert.ThrowsAsync<DataPackageException>(() => application.BuildAsync(agent, Quarter));

                // It says which part, and how the read ended.
                Assert.Equal((missing, outcome), (refused.Missing, refused.Outcome));
                Assert.Equal(
                    $"No data package is built: the exchange did not give {missing} ({outcome}). The cycle makes no decision. " +
                    "See docs/cycles.md.",
                    refused.Message);
            }
        }

        // The first part that is missing is said, in the order of the package.
        market.Candles = _ => StandInMarket.NoCandles(ExchangeCallOutcome.TimedOut);
        market.MarkPriceOutcome = ExchangeCallOutcome.Failed;

        Assert.Equal("the 1H candles", (await Assert.ThrowsAsync<DataPackageException>(() => application.BuildAsync(agent, Quarter))).Missing);

        // Once the exchange answers again, so is a package built.
        market.Candles = null;
        market.MarkPriceOutcome = ExchangeCallOutcome.Succeeded;
        market.OpenInterestOutcome = ExchangeCallOutcome.Succeeded;

        Assert.NotNull(await application.BuildAsync(agent, Quarter));
    }

    [Fact]
    public async Task An_equity_that_is_not_known_gives_no_package()
    {
        // The exchange did not say the price that the open position of the agent is valued at:
        // what keeps the positions says that its equity is not known (FR-RSK-8).
        var position = new PositionContext(PositionSide.Long, EntryPrice: 61000m, Quantity: 0.015m, StopLoss: 59500m, TakeProfit: 64000m);
        var holdings = new StandInHoldings(
            new AgentHoldings(Equity: 1000m, position, Orders: [], EquityAtDayStart: 1000m, PeakEquity: 1000m, Valued: false));

        using var application = new PackagesApplication(Quarter, holdings);
        var agent = PackageAgents.On(Market.Spot, Timeframe.FifteenMinutes);

        // No prompt states an equity that is a guess: the cycle makes no decision, and the
        // position stays as it is, with its protective orders.
        var refused = await Assert.ThrowsAsync<DataPackageException>(() => application.BuildAsync(agent, Quarter));

        Assert.Equal("a price that the equity of the agent is worked out from", refused.Missing);

        // Once it is known again, so is a package built.
        holdings.Holds = holdings.Holds with { Valued = true };

        Assert.NotNull(await application.BuildAsync(agent, Quarter));
    }

    [Fact]
    public async Task A_higher_timeframe_that_has_no_closed_candle_yet_is_an_empty_series_and_no_failure()
    {
        using var application = new PackagesApplication(Monday);

        // A symbol that is new to the exchange: its first week has not closed.
        application.Market.Candles = asked =>
            asked.Interval == CandleInterval.OneWeek ? new ExchangeCandles(ExchangeCallOutcome.Succeeded, [], Monday) : null;

        var package = await application.BuildAsync(PackageAgents.On(Market.Spot, Timeframe.OneDay), Monday);

        var week = Assert.Single(package.Context);
        Assert.Equal(CandleInterval.OneWeek, week.Interval);
        Assert.Empty(week.Candles);
        Assert.Equal(0, week.ComputedFrom);
        Assert.Equal(new IndicatorValues(null, null, null, null, null, null, null, null), week.Indicators);
        Assert.Contains(
            "\n\nContext, closed 1W candles: none, the exchange has closed no candle of that length yet\n\n",
            package.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_package_is_built_from_candles_that_do_not_reach_the_close_of_the_cycle()
    {
        using var application = new PackagesApplication(Quarter);
        var agent = PackageAgents.On(Market.Spot, Timeframe.FifteenMinutes);
        var packages = application.Application.DataPackages;

        // What starts the cycles hands a cycle the candles through its close, and never others
        // (FR-CYC-1): candles that come one short, or none, are no start of a package.
        var oneShort = PackagesApplication.CycleOf(agent, Quarter) with
        {
            Candles = StandInMarket.Through(Quarter.AddMinutes(-15), CandleInterval.FifteenMinutes),
        };
        var none = oneShort with { Candles = StandInMarket.NoCandles(ExchangeCallOutcome.TimedOut) };

        await Assert.ThrowsAsync<ArgumentException>(() => packages.BuildAsync(oneShort, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => packages.BuildAsync(none, TestContext.Current.CancellationToken));
        Assert.Empty(application.Market.Asked);
    }

    [Fact]
    public async Task A_role_reads_the_package_as_one_text_that_is_the_same_for_the_same_package()
    {
        var holdings = new StandInHoldings(new AgentHoldings(
            Equity: 1040.5m,
            new PositionContext(PositionSide.Long, 61000m, 0.015m, 59500m, 64000m),
            [
                new OpenOrder(OpenOrderKind.Entry, OrderSide.Buy, 0.01m, 60250.5m),
                new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Sell, 0.015m, 59500m),
                new OpenOrder(OpenOrderKind.TakeProfit, OrderSide.Sell, 0.015m, 64000m),
            ],
            EquityAtDayStart: 1040.5m,
            PeakEquity: 1040.5m));

        using var application = new PackagesApplication(Monday, holdings, candles: 2, contextCandles: 2);
        var packages = application.Application.DataPackages;

        // Three days through the Monday, and the one week that closed with them.
        ExchangeCandle[] days =
        [
            new(Monday.AddDays(-3), Monday.AddDays(-2), 60000m, 60500.5m, 59800m, 60250.25m, 1200.5m),
            new(Monday.AddDays(-2), Monday.AddDays(-1), 60250.25m, 61000m, 60100m, 60900m, 980m),
            new(Monday.AddDays(-1), Monday, 60900m, 61420m, 60880.25m, 61310m, 1842.337m),
        ];
        ExchangeCandle[] weeks = [new(Monday.AddDays(-7), Monday, 58000m, 61420m, 57500.75m, 61310m, 9500.125m)];

        application.Market.Candles = _ => new ExchangeCandles(ExchangeCallOutcome.Succeeded, weeks, Monday);

        CycleStart CycleOf(Market market) => new(
            Guid.CreateVersion7(),
            PackageAgents.On(market, Timeframe.OneDay),
            Roles: [],
            Monday,
            new ExchangeCandles(ExchangeCallOutcome.Succeeded, days, Monday));

        const string NoIndicators =
            "- Moving averages of the close: SMA20 not available, SMA50 not available, SMA200 not available, " +
            "EMA20 not available, EMA50 not available\n" +
            "- Relative strength: RSI14 not available\n" +
            "- Average true range: ATR14 not available\n";

        const string OnSpot =
            "Closed 1D candles: 2, oldest first, through 2026-11-16T00:00Z\n" +
            "Each line: close time (UTC), open, high, low, close, volume\n" +
            "2026-11-15T00:00Z, 60250.25, 61000, 60100, 60900, 980\n" +
            "2026-11-16T00:00Z, 60900, 61420, 60880.25, 61310, 1842.337\n" +
            "Indicators at that close, computed by the system from 3 closed 1D candles:\n" +
            NoIndicators +
            "- Volume: 1842.337 in the latest candle, average of 20 candles not available\n" +
            "\n" +
            "Context, closed 1W candles: 1, oldest first, through 2026-11-16T00:00Z\n" +
            "2026-11-16T00:00Z, 58000, 61420, 57500.75, 61310, 9500.125\n" +
            "Indicators at that close, computed by the system from 1 closed 1W candle:\n" +
            NoIndicators +
            "- Volume: 9500.125 in the latest candle, average of 20 candles not available\n" +
            "\n" +
            "Open orders:\n" +
            "- entry: buy 0.01 at 60250.5\n" +
            "- stop-loss: sell 0.015 at 59500\n" +
            "- take-profit: sell 0.015 at 64000";

        var onSpot = await packages.BuildAsync(CycleOf(Market.Spot), TestContext.Current.CancellationToken);
        var onFutures = await packages.BuildAsync(CycleOf(Market.Futures), TestContext.Current.CancellationToken);

        Assert.Equal(OnSpot, onSpot.Text);

        // Futures adds one line, and nothing else differs.
        Assert.Equal(
            OnSpot + "\n\nFutures: mark price 61888.1, funding rate 0.01%, next funding 2026-11-16T08:00Z, open interest 82450.5 BTC",
            onFutures.Text);

        // The same package gives the same text, in whatever country the service runs: a line
        // ends with a line feed, and a number is written with a point and no separator.
        var culture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal(OnSpot, (await packages.BuildAsync(CycleOf(Market.Spot), TestContext.Current.CancellationToken)).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        Assert.DoesNotContain('\r', onFutures.Text);
    }

    [Fact]
    public async Task The_package_of_every_market_and_timeframe_is_composed_into_the_prompt_of_every_role()
    {
        var holdings = new StandInHoldings(new AgentHoldings(
            Equity: 1040.5m,
            new PositionContext(PositionSide.Long, 61000m, 0.015m, 59500m, 64000m),
            [new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Sell, 0.015m, 59500m)],
            EquityAtDayStart: 1040.5m,
            PeakEquity: 1040.5m));

        using var application = new PackagesApplication(Monday, holdings);

        foreach (var market in Enum.GetValues<Market>())
        {
            foreach (var timeframe in Timeframes.All)
            {
                var agent = PackageAgents.On(market, timeframe);
                var package = await application.BuildAsync(agent, Monday);

                // No line of the package reads as a heading of a prompt.
                Assert.DoesNotContain(package.Text.Split('\n'), line => line.StartsWith('#'));

                foreach (var role in Enum.GetValues<Role>())
                {
                    // Nothing in the package is refused as a secret, an address, or an identifier.
                    var prompt = await application.Application.PromptComposition.ComposeAsync(
                        role, package.PartsWith([]), TestContext.Current.CancellationToken);

                    // The package is the part of the prompt that is headed as the data package,
                    // as it was written, and the context of the agent is stated from the same.
                    Assert.Equal(Headings, HeadingsOf(prompt.Text));
                    Assert.Equal(package.Text, PartOf(prompt.Text, "Data package"));
                    Assert.Contains("Equity: 1040.5 USDT\n", PartOf(prompt.Text, "Agent context"), StringComparison.Ordinal);
                    Assert.Contains(
                        "Position: long, quantity 0.015, entry price 61000, stop-loss 59500, take-profit 64000\n",
                        PartOf(prompt.Text, "Agent context"),
                        StringComparison.Ordinal);

                    // It says nothing of whose agent it is (FR-PRM-9).
                    Assert.DoesNotContain(agent.Name, prompt.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("Lovelace", prompt.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(agent.Id.ToString(), prompt.Text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(agent.UserId.ToString(), prompt.Text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void A_package_holds_no_more_candles_of_a_timeframe_than_one_read_of_the_exchange_gives()
    {
        // The settings are held to it when the file is read. They are of the platform, which
        // may not name the gateway, so the number is written there again.
        Assert.Equal(ExchangeCandles.Most, DataPackageSettings.MostCandles);
    }

    /// <summary>Whether two sets of the parts of a prompt are the same, lesson by lesson.</summary>
    private static bool PartsAreTheSame(PromptParts expected, PromptParts actual) =>
        expected.Agent == actual.Agent && expected.DataPackage == actual.DataPackage && expected.Lessons.SequenceEqual(actual.Lessons);
}
