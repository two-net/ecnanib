using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.DecisionEngine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// The calls of the analysts are scored against the price change over the next 4 closed candles
/// (FR-EXP-3), and vetoed plans are evaluated as shadow trades against later closed candles,
/// with standard fees. Shadow trades never place orders (FR-EXP-5). A job does both, from the
/// candles that the exchange gives. See "The job" in docs/experience.md.
/// </summary>
public class ExperienceScoringJobTests
{
    private const string Ada = "ada@example.com";

    private const string Bearish =
        """{"bias":"bearish","notableEvents":[],"confidence":0.7,"liveDataAvailable":true,"rationale":"The headlines turned."}""";

    // The close of the candle that the cycles of these tests are for, whose last price is 61310
    // in the summary of their data package. Their agents work on the hour.
    private static readonly DateTimeOffset Close = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private const decimal PriceAtClose = 61310m;

    // The fee of an order on the Spot market in the file of the tests: 0.2 percent.
    private const decimal Fee = 0.002m;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_calls_are_scored_and_the_shadow_trades_followed_every_few_minutes()
    {
        var clock = new TestClock(Close);
        using var application = Application(clock, new StandInCandles(clock));
        var job = application.ExperienceScoring;

        Assert.Equal("experience-scoring", job.Name);
        Assert.Equal("every 00:05:00", job.Schedule.ToString());
        Assert.Equal(Close.AddMinutes(5), job.Schedule.NextRun(Close, Close.AddSeconds(30)));
    }

    [Fact]
    public async Task A_call_is_scored_once_its_fourth_later_candle_has_closed_and_not_before()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // The Market Analyst called up from 61310, and the Sentiment Analyst down.
        await application.GiveCycleAsync(
            ada.Id, agent, Close, orders: false, answers: role => role == Role.SentimentAnalyst ? Bearish : TeamAnswers.Of(role));

        // Nothing is due before the 4th candle after the call can have closed: the exchange is asked nothing.
        await application.ScoreExperienceAsync();
        clock.Set(Close.AddHours(4).AddSeconds(-1));
        await application.ScoreExperienceAsync();

        Assert.Empty(exchange.Asked);
        Assert.All(await CallsAsync(application), call => Assert.Null(call.Outcome));

        // It is due now, and the exchange has not closed the 4th candle yet: it gives three
        // later ones. The call waits, and is asked about again with the next run.
        clock.Set(Close.AddHours(4));
        exchange.Answer = _ => Through(Close, 60900m, 61800m, 62400m);
        await application.ScoreExperienceAsync();

        Assert.Equal([new AskedCandles(Market.Spot, TradingMode.Demo, "BTCUSDT", CandleInterval.OneHour)], exchange.Asked);
        Assert.All(await CallsAsync(application), call => Assert.Null(call.Outcome));

        // The 4th candle closed at 61500, above the price of the call, whatever the three
        // before it did: the call up matched, and the call down did not.
        clock.Set(Close.AddHours(4).AddMinutes(5));
        exchange.Answer = _ => Through(Close, 60900m, 61800m, 62400m, 61500m);
        await application.ScoreExperienceAsync();

        var scored = (await CallsAsync(application)).ToDictionary(call => call.Role);

        Assert.Equal(
            (CallOutcome.Matched, 61500m, Close.AddHours(4).AddMinutes(5)),
            (scored[Role.MarketAnalyst].Outcome, scored[Role.MarketAnalyst].LaterPrice, scored[Role.MarketAnalyst].ScoredAt));
        Assert.Equal((CallOutcome.Missed, 61500m), (scored[Role.SentimentAnalyst].Outcome, scored[Role.SentimentAnalyst].LaterPrice));

        // A call is scored once: nothing is due any more, and a later run asks nothing, also
        // once the exchange gives a 5th candle that went the other way.
        clock.Set(Close.AddHours(5));
        exchange.Answer = _ => Through(Close, 60900m, 61800m, 62400m, 61500m, 59000m);
        await application.ScoreExperienceAsync();

        Assert.Equal(2, exchange.Asked.Count);
        Assert.Equal(CallOutcome.Matched, (await CallsAsync(application)).Single(call => call.Role == Role.MarketAnalyst).Outcome);
    }

    [Fact]
    public async Task A_shadow_trade_is_followed_until_a_later_candle_ends_it_and_places_no_order()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // A long at the market from 61310, wrong at 59000 and done at 64000, which was vetoed.
        await application.GiveCycleAsync(ada.Id, agent, Close, CycleOutcome.Vetoed, orders: false);

        // The first later candle stayed between the prices: the trade is open, and is looked at
        // again once the next candle has closed.
        clock.Set(Close.AddHours(1));
        exchange.Answer = _ => Candles(Close, (61000m, 62000m, 61900m));
        await application.ScoreExperienceAsync();

        var open = Assert.Single(await ShadowTradesAsync(application));

        Assert.Null(open.Outcome);
        Assert.Equal(Close.AddHours(2), open.DueAt);

        // So a run before that asks nothing of it. Its call is not due either.
        clock.Set(Close.AddHours(1).AddMinutes(5));
        await application.ScoreExperienceAsync();

        Assert.Single(exchange.Asked);

        // The third later candle reached the take-profit, and the stop-loss was never reached:
        // it made 2690, less the fee on its entry and on its exit, for the 2310 that it risked.
        clock.Set(Close.AddHours(3));
        exchange.Answer = _ => Candles(Close, (61000m, 62000m, 61900m), (60500m, 63000m, 62800m), (62500m, 64010m, 63900m));
        await application.ScoreExperienceAsync();

        var ended = Assert.Single(await ShadowTradesAsync(application));

        Assert.Equal((ShadowOutcome.TakeProfit, Close.AddHours(3), Close.AddHours(3)), (ended.Outcome, ended.ExitedAt, ended.EvaluatedAt));
        Assert.Equal(Math.Round((2690m - (Fee * (PriceAtClose + 64000m))) / 2310m, 12, MidpointRounding.AwayFromZero), ended.ResultInR);
        Assert.Equal(1.056008658009m, ended.ResultInR);

        // It ended once. A candle that falls through the stop-loss afterwards changes nothing.
        clock.Set(Close.AddHours(4));
        exchange.Answer = _ => Candles(
            Close, (61000m, 62000m, 61900m), (60500m, 63000m, 62800m), (62500m, 64010m, 63900m), (58000m, 63900m, 58500m));
        await application.ScoreExperienceAsync();

        Assert.Equal(ShadowOutcome.TakeProfit, Assert.Single(await ShadowTradesAsync(application)).Outcome);

        // A shadow trade never places an order: the exchange was asked for candles alone, and
        // no order, no execution, and no position was written for it.
        Assert.All(exchange.Asked, asked => Assert.Equal(new AskedCandles(Market.Spot, TradingMode.Demo, "BTCUSDT", CandleInterval.OneHour), asked));
        Assert.Equal(0, application.Database.Scalar<long>("SELECT count(*) FROM orders"));
        Assert.Equal(0, application.Database.Scalar<long>("SELECT count(*) FROM fills"));
        Assert.Equal(0, application.Database.Scalar<long>("SELECT count(*) FROM positions"));
    }

    [Fact]
    public async Task The_stop_loss_is_taken_to_be_hit_first_when_a_candle_reaches_both_prices()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveCycleAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Close), Close, CycleOutcome.Vetoed, orders: false);

        // One candle from below the stop-loss to above the take-profit.
        clock.Set(Close.AddHours(1));
        exchange.Answer = _ => Candles(Close, (58900m, 64100m, 63000m));
        await application.ScoreExperienceAsync();

        var stopped = Assert.Single(await ShadowTradesAsync(application));

        // It lost the 2310 that it risked, and its fees on top.
        Assert.Equal(ShadowOutcome.StopLoss, stopped.Outcome);
        Assert.Equal(Math.Round((-2310m - (Fee * (PriceAtClose + 59000m))) / 2310m, 12, MidpointRounding.AwayFromZero), stopped.ResultInR);
        Assert.True(stopped.ResultInR < -1m);
    }

    [Fact]
    public async Task An_entry_at_a_price_that_the_next_candle_does_not_reach_ends_with_no_result()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);

        // A long that waits at 60500, and a market that stayed above it for the next candle.
        await application.GiveCycleAsync(
            ada.Id,
            agent,
            Close,
            CycleOutcome.Vetoed,
            orders: false,
            answers: role => role == Role.Strategist ? TeamAnswers.Plan(entryType: "limit", entryPrice: 60500m) : TeamAnswers.Of(role));

        clock.Set(Close.AddHours(2));
        exchange.Answer = _ => Candles(Close, (60600m, 62000m, 61000m), (58000m, 65000m, 60000m));
        await application.ScoreExperienceAsync();

        var trade = Assert.Single(await ShadowTradesAsync(application));

        // An entry that rests is taken off when the next candle of its agent has closed.
        Assert.Equal((ShadowOutcome.NotEntered, null, Close.AddHours(1)), (trade.Outcome, trade.ResultInR, trade.ExitedAt));
    }

    [Fact]
    public async Task A_shadow_trade_that_reaches_neither_price_is_closed_at_the_last_close_of_the_candles_that_it_is_followed_for()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveCycleAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Close), Close, CycleOutcome.Vetoed, orders: false);

        // 199 candles between the prices: it is open, and is looked at when the 200th has closed.
        var quiet = Enumerable.Repeat((60000m, 63000m, 61000m), 199).ToArray();

        clock.Set(Close.AddHours(199));
        exchange.Answer = _ => Candles(Close, quiet);
        await application.ScoreExperienceAsync();

        var open = Assert.Single(await ShadowTradesAsync(application));

        Assert.Null(open.Outcome);
        Assert.Equal(Close.AddHours(200), open.DueAt);

        // The 200th closed at 62000: the trade is closed there, 690 above its entry.
        clock.Set(Close.AddHours(200));
        exchange.Answer = _ => Candles(Close, [.. quiet, (60000m, 63000m, 62000m)]);
        await application.ScoreExperienceAsync();

        var ended = Assert.Single(await ShadowTradesAsync(application));

        Assert.Equal((ShadowOutcome.TimeUp, Close.AddHours(200)), (ended.Outcome, ended.ExitedAt));
        Assert.Equal(Math.Round((690m - (Fee * (PriceAtClose + 62000m))) / 2310m, 12, MidpointRounding.AwayFromZero), ended.ResultInR);
    }

    [Fact]
    public async Task A_read_that_fails_leaves_everything_for_the_next_run()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock) { Answer = _ => StandInCandles.NoAnswer };
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveCycleAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Close), Close, CycleOutcome.Vetoed, orders: false);

        clock.Set(Close.AddHours(4));
        var before = application.EverythingStored();

        // The exchange gave no answer. Nothing is written, and the run fails nothing.
        await application.ScoreExperienceAsync();

        Assert.Equal(before, application.EverythingStored());
        Assert.Contains(
            application.Events(),
            logged => logged.Template == "The candles of {Symbol} on {Market} in {TradingMode} could not be read for Experience ({ExchangeCallOutcome}). What it was to score of it waits for the next run");

        // The next run finds both rows due still, and the exchange answers.
        exchange.Answer = _ => Through(Close, 61000m, 61500m, 62000m, 62500m);
        await application.ScoreExperienceAsync();

        Assert.Equal(CallOutcome.Matched, Assert.Single(await CallsAsync(application)).Outcome);
        Assert.Null(Assert.Single(await ShadowTradesAsync(application)).Outcome);
    }

    [Fact]
    public async Task Candles_that_are_no_longer_given_end_a_row_with_no_result()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveCycleAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Close), Close, CycleOutcome.Vetoed, orders: false);

        // The service was down for longer than the candles of the exchange reach back: the
        // oldest that it gives began after the cycle's own had closed, so the first later one
        // is gone, and what is left does not say what the price did.
        clock.Set(Close.AddDays(40));
        exchange.Answer = _ => Candles(Close.AddDays(39), (60000m, 63000m, 61000m), (60000m, 63000m, 61000m));
        await application.ScoreExperienceAsync();

        var call = Assert.Single(await CallsAsync(application));
        var trade = Assert.Single(await ShadowTradesAsync(application));

        Assert.Equal((CallOutcome.NoCandles, null, Close.AddDays(40)), (call.Outcome, call.LaterPrice, call.ScoredAt));
        Assert.Equal((ShadowOutcome.NoCandles, null, null, Close.AddDays(40)), (trade.Outcome, trade.ResultInR, trade.ExitedAt, trade.EvaluatedAt));
    }

    [Fact]
    public async Task A_symbol_that_the_configuration_file_no_longer_lists_ends_its_rows_with_no_result()
    {
        var clock = new TestClock(Close);

        // The gateway serves the symbols of the file alone, and answers an ask for another with an error.
        var exchange = new StandInCandles(clock) { Answer = _ => throw new ArgumentException("The symbol is not one of Binance:Symbols.") };
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveCycleAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Close), Close, CycleOutcome.Vetoed, orders: false);

        clock.Set(Close.AddHours(4));
        await application.ScoreExperienceAsync();

        Assert.Equal(CallOutcome.NoCandles, Assert.Single(await CallsAsync(application)).Outcome);
        Assert.Equal(ShadowOutcome.NoCandles, Assert.Single(await ShadowTradesAsync(application)).Outcome);
    }

    [Fact]
    public async Task A_symbol_of_which_no_candle_is_given_waits_until_its_candles_could_no_longer_come()
    {
        var clock = new TestClock(Close);

        // The exchange answers, and has no candle of the symbol at all.
        var exchange = new StandInCandles(clock) { Answer = _ => new ExchangeCandles(ExchangeCallOutcome.Succeeded, [], clock.GetUtcNow()) };
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveCycleAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Close), Close, CycleOutcome.Vetoed, orders: false);

        // Nothing says what the price did, and nothing says that it never will: the rows wait.
        clock.Set(Close.AddHours(ExchangeCandles.Most));
        await application.ScoreExperienceAsync();

        Assert.Null(Assert.Single(await CallsAsync(application)).Outcome);
        Assert.Null(Assert.Single(await ShadowTradesAsync(application)).Outcome);

        // The exchange gives so many candles of a length and no more. Once the cycle is longer
        // ago than they last, its later candles will not be given any more, and the rows end.
        clock.Set(Close.AddHours(ExchangeCandles.Most).AddMinutes(5));
        await application.ScoreExperienceAsync();

        Assert.Equal(CallOutcome.NoCandles, Assert.Single(await CallsAsync(application)).Outcome);
        Assert.Equal(ShadowOutcome.NoCandles, Assert.Single(await ShadowTradesAsync(application)).Outcome);
    }

    [Fact]
    public async Task Candles_are_asked_for_once_for_each_symbol_market_mode_and_timeframe()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);

        // Three cycles of one agent, one of an agent of another symbol, one on Futures in Live,
        // and one on another timeframe: 6 calls and 6 shadow trades, of 4 series of candles.
        var hourly = await application.GiveAgentAsync(ada.Id, Close, name: "Hourly");
        var other = await application.GiveAgentAsync(ada.Id, Close, name: "Other", symbol: "SOLUSDT");
        var futures = await application.GiveAgentAsync(ada.Id, Close, name: "Futures", market: Market.Futures, mode: TradingMode.Live);
        var quarterly = await application.GiveAgentAsync(ada.Id, Close, name: "Quarterly", symbol: "ETHUSDT", timeframe: Timeframe.FifteenMinutes);

        foreach (var (agent, closedAt) in new[]
        {
            (hourly, Close), (hourly, Close.AddHours(1)), (hourly, Close.AddHours(2)), (other, Close), (futures, Close), (quarterly, Close),
        })
        {
            await application.GiveCycleAsync(ada.Id, agent, closedAt, CycleOutcome.Vetoed, orders: false);
        }

        clock.Set(Close.AddHours(8));
        exchange.Answer = asked => asked.Interval == CandleInterval.OneHour
            ? Candles(Close, [.. Enumerable.Repeat((60000m, 63000m, 61400m), 8)])
            : Candles(Close, TimeSpan.FromMinutes(15), [.. Enumerable.Repeat((60000m, 63000m, 61400m), 32)]);
        await application.ScoreExperienceAsync();

        Assert.Equal(
            [
                new AskedCandles(Market.Spot, TradingMode.Demo, "BTCUSDT", CandleInterval.OneHour),
                new AskedCandles(Market.Spot, TradingMode.Demo, "ETHUSDT", CandleInterval.FifteenMinutes),
                new AskedCandles(Market.Spot, TradingMode.Demo, "SOLUSDT", CandleInterval.OneHour),
                new AskedCandles(Market.Futures, TradingMode.Live, "BTCUSDT", CandleInterval.OneHour),
            ],
            exchange.Asked.OrderBy(asked => asked.Market).ThenBy(asked => asked.Symbol, StringComparer.Ordinal));

        // Every call is scored from them: the price was above 61310 after 4 candles, as each called.
        Assert.All(await CallsAsync(application), call => Assert.Equal(CallOutcome.Matched, call.Outcome));
        Assert.Equal(6, (await CallsAsync(application)).Count);

        // And every shadow trade is open, and waits for the next candle of its own length.
        var trades = await ShadowTradesAsync(application);

        Assert.All(trades, trade => Assert.Null(trade.Outcome));
        Assert.Equal(
            [Close.AddHours(8).AddMinutes(15), Close.AddHours(9), Close.AddHours(9), Close.AddHours(9), Close.AddHours(9), Close.AddHours(9)],
            trades.Select(trade => trade.DueAt).Order());
    }

    [Fact]
    public async Task A_row_that_went_with_its_agent_while_the_candles_were_read_fails_nothing()
    {
        var clock = new TestClock(Close);
        var exchange = new StandInCandles(clock);
        using var application = Application(clock, exchange);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Close);
        await application.GiveCycleAsync(ada.Id, agent, Close, CycleOutcome.Vetoed, orders: false);

        // Its user deletes the agent as the run waits for the exchange: the rows that the run
        // was to complete are gone, and it completes nothing.
        clock.Set(Close.AddHours(4));
        exchange.Answer = _ =>
        {
            application.Database.Execute($"DELETE FROM agents WHERE id = '{agent}'");

            return Through(Close, 61000m, 61500m, 62000m, 65000m);
        };

        await application.ScoreExperienceAsync();

        Assert.Empty(await CallsAsync(application));
        Assert.Empty(await ShadowTradesAsync(application));
    }

    /// <summary>The application, with what stands for the candles of the exchange.</summary>
    private static AccountsApplication Application(TestClock clock, StandInCandles exchange) =>
        AgentsApplication.With(
            new StandInMarketData(),
            clock,
            host: builder => builder.ConfigureServices(services => services.AddSingleton<IExchangeMarketData>(exchange)));

    /// <summary>
    /// What the exchange gives: the candle that closed at a time and the one before it, and
    /// then one later candle of an hour for each close, each from 500 below its close to 500 above.
    /// </summary>
    private static ExchangeCandles Through(DateTimeOffset closedAt, params decimal[] laterCloses) =>
        Candles(closedAt, [.. laterCloses.Select(close => (close - 500m, close + 500m, close))]);

    private static ExchangeCandles Candles(DateTimeOffset closedAt, params (decimal Low, decimal High, decimal Close)[] later) =>
        Candles(closedAt, TimeSpan.FromHours(1), later);

    /// <summary>
    /// What the exchange gives: the candle that closed at a time and the one before it, and
    /// then the later candles that a test names, oldest first.
    /// </summary>
    private static ExchangeCandles Candles(DateTimeOffset closedAt, TimeSpan length, params (decimal Low, decimal High, decimal Close)[] later)
    {
        (decimal Low, decimal High, decimal Close)[] all = [(60800m, 61400m, 61250m), (60880.25m, 61420m, PriceAtClose), .. later];

        return new ExchangeCandles(
            ExchangeCallOutcome.Succeeded,
            [
                .. all.Select((candle, index) => new ExchangeCandle(
                    closedAt + (length * (index - 2)),
                    closedAt + (length * (index - 1)),
                    Open: candle.Close,
                    candle.High,
                    candle.Low,
                    candle.Close,
                    Volume: 12.5m)),
            ],
            closedAt + (length * (all.Length - 2)));
    }

    private static async Task<IReadOnlyList<AnalystCall>> CallsAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<AnalystCall>().AsNoTracking().OrderBy(call => call.ClosedAt).ThenBy(call => call.Role).ToListAsync(Cancellation);
    }

    private static async Task<IReadOnlyList<ShadowTrade>> ShadowTradesAsync(AccountsApplication application)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<ShadowTrade>().AsNoTracking().OrderBy(trade => trade.ClosedAt).ToListAsync(Cancellation);
    }
}
