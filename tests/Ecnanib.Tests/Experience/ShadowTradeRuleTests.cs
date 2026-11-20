using Ecnanib.ExchangeGateway;
using Ecnanib.Experience;
using Ecnanib.Shared;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// Vetoed plans are evaluated as shadow trades against later closed candles, using their own
/// entry, stop-loss, and take-profit with standard fees, and assuming the stop-loss was hit
/// first when both fall inside one candle (FR-EXP-5). See "Shadow trades" in docs/experience.md.
/// </summary>
public class ShadowTradeRuleTests
{
    // The close of the candle that the vetoed plan was made on, and the fee of an order on the
    // Spot market in the tracked file, as a fraction.
    private static readonly DateTimeOffset Close = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private const decimal Fee = 0.001m;
    private const decimal NoFee = 0m;

    // A long from 100, wrong at 95 and done at 110: it risks 5 to make 10.
    private const decimal Entry = 100m;
    private const decimal Stop = 95m;
    private const decimal Target = 110m;

    [Fact]
    public void The_stop_loss_is_taken_to_be_hit_first_when_both_prices_fall_inside_one_candle()
    {
        // One candle from 94 to 111 reached both. It does not say which it reached first, and the
        // worse of the two is assumed.
        var result = Long([Candle(1, low: 94m, high: 111m)], NoFee);

        Assert.Equal(new ShadowResult(ShadowOutcome.StopLoss, -1m, Close.AddHours(1)), result);

        // So is it for a short, which is wrong above its entry and done below.
        Assert.Equal(
            new ShadowResult(ShadowOutcome.StopLoss, -1m, Close.AddHours(1)),
            ShadowTradeRule.Of(ShadowSide.Short, Entry, resting: false, stopLoss: 105m, takeProfit: 90m, [Candle(1, low: 89m, high: 106m)], NoFee));
    }

    [Fact]
    public void A_trade_ends_at_the_first_of_its_prices_that_a_later_candle_reaches()
    {
        // Two candles between the prices, and then one that reaches the take-profit: 10 for a risk of 5.
        Assert.Equal(
            new ShadowResult(ShadowOutcome.TakeProfit, 2m, Close.AddHours(3)),
            Long([Candle(1, 97m, 104m), Candle(2, 99m, 109.99m), Candle(3, 101m, 110m), Candle(4, 90m, 120m)], NoFee));

        // And one that reaches the stop-loss. What the candles after it did says nothing more.
        Assert.Equal(
            new ShadowResult(ShadowOutcome.StopLoss, -1m, Close.AddHours(2)),
            Long([Candle(1, 97m, 104m), Candle(2, 95m, 103m), Candle(3, 101m, 130m)], NoFee));

        // A short gains when the price falls: done at 90, from 100, for a risk of 5.
        Assert.Equal(
            new ShadowResult(ShadowOutcome.TakeProfit, 2m, Close.AddHours(2)),
            ShadowTradeRule.Of(
                ShadowSide.Short, Entry, resting: false, stopLoss: 105m, takeProfit: 90m, [Candle(1, 96m, 104.99m), Candle(2, 90m, 99m)], NoFee));
    }

    [Fact]
    public void A_trade_ends_at_its_own_prices_whatever_a_candle_opens_at()
    {
        // The candle opened below the stop-loss, and the trade still ends at the stop-loss: a
        // shadow trade uses the prices of its plan.
        Assert.Equal(-1m, Long([new ExchangeCandle(Close, Close.AddHours(1), Open: 90m, High: 92m, Low: 88m, Close: 91m, Volume: 1m)], NoFee)!.ResultInR);
    }

    [Fact]
    public void Fees_are_taken_on_the_entry_and_on_the_exit()
    {
        // Stopped at 95: it lost 5, and paid a thousandth of 100 and of 95, over a risk of 5.
        Assert.Equal(-1.039m, Long([Candle(1, low: 94m, high: 101m)], Fee)!.ResultInR);

        // Done at 110: it made 10, and paid a thousandth of 100 and of 110.
        Assert.Equal(1.958m, Long([Candle(1, low: 99m, high: 110m)], Fee)!.ResultInR);

        // The fee of the Futures market in the tracked file is half of it.
        Assert.Equal(1.979m, Long([Candle(1, low: 99m, high: 110m)], feeRate: 0.0005m)!.ResultInR);

        // A short pays the same on what its entry and its exit are worth: 100 and 90.
        Assert.Equal(
            1.962m,
            ShadowTradeRule.ResultInR(ShadowSide.Short, entryPrice: 100m, exitPrice: 90m, stopLoss: 105m, Fee));
    }

    [Fact]
    public void An_entry_at_the_market_is_entered_at_once_so_its_first_candle_counts_whole()
    {
        // The first later candle reached the take-profit: the trade was in from its first price.
        Assert.Equal(ShadowOutcome.TakeProfit, Long([Candle(1, low: 99m, high: 112m)], NoFee)!.Outcome);
    }

    [Fact]
    public void An_entry_at_a_price_that_the_next_candle_does_not_reach_is_not_entered()
    {
        // A long that waits at 100 for a market that stayed above it: the entry is taken off
        // when the next candle of the agent has closed, so what came later says nothing.
        var result = ShadowTradeRule.Of(
            ShadowSide.Long, Entry, resting: true, Stop, Target, [Candle(1, low: 100.01m, high: 104m), Candle(2, low: 90m, high: 120m)], Fee);

        Assert.Equal(new ShadowResult(ShadowOutcome.NotEntered, null, Close.AddHours(1)), result);

        // A short waits for the market to rise to its price.
        Assert.Equal(
            ShadowOutcome.NotEntered,
            ShadowTradeRule.Of(ShadowSide.Short, Entry, resting: true, stopLoss: 105m, takeProfit: 90m, [Candle(1, low: 95m, high: 99.99m)], Fee)!.Outcome);
    }

    [Fact]
    public void In_the_candle_that_enters_an_entry_at_a_price_only_the_stop_loss_counts()
    {
        // The candle went from 112 down to 99.5: it was at the take-profit before it came to
        // the entry, for all it says. So the trade is entered, and not done. The next candle is.
        Assert.Equal(
            new ShadowResult(ShadowOutcome.TakeProfit, 2m, Close.AddHours(2)),
            ShadowTradeRule.Of(
                ShadowSide.Long, Entry, resting: true, Stop, Target, [Candle(1, low: 99.5m, high: 112m), Candle(2, low: 101m, high: 110m)], NoFee));

        // A candle that came down to the stop-loss came through the entry first: it is entered and stopped.
        Assert.Equal(
            new ShadowResult(ShadowOutcome.StopLoss, -1m, Close.AddHours(1)),
            ShadowTradeRule.Of(ShadowSide.Long, Entry, resting: true, Stop, Target, [Candle(1, low: 94m, high: 112m)], NoFee));

        // Entered by its first candle, and neither price since: it is open.
        Assert.Null(ShadowTradeRule.Of(ShadowSide.Long, Entry, resting: true, Stop, Target, [Candle(1, low: 99.5m, high: 112m)], NoFee));
    }

    [Fact]
    public void A_trade_that_reaches_neither_price_ends_at_the_last_close_of_the_candles_that_it_is_followed_for()
    {
        Assert.Equal(200, ShadowTradeRule.CandlesFollowed);

        // 200 candles between the prices, the last of which closed at 104: it is closed there,
        // for 4 of the 5 that it risked, less its fees.
        var quiet = Enumerable.Range(1, 200).Select(index => Candle(index, low: 97m, high: 106m, close: index == 200 ? 104m : 101m)).ToList();

        Assert.Equal(new ShadowResult(ShadowOutcome.TimeUp, 0.8m, Close.AddHours(200)), Long(quiet, NoFee));
        Assert.Equal(0.7592m, Long(quiet, Fee)!.ResultInR);

        // One candle fewer, and it is open still.
        Assert.Null(Long([.. quiet.Take(199)], NoFee));

        // What came after the 200th is not looked at: a 201st that reaches the take-profit changes nothing.
        Assert.Equal(ShadowOutcome.TimeUp, Long([.. quiet, Candle(201, low: 100m, high: 150m)], NoFee)!.Outcome);
    }

    [Fact]
    public void A_trade_is_open_while_no_later_candle_has_ended_it()
    {
        Assert.Null(Long([], Fee));
        Assert.Null(Long([Candle(1, low: 96m, high: 109m)], Fee));
        Assert.Null(ShadowTradeRule.Of(ShadowSide.Long, Entry, resting: true, Stop, Target, [], Fee));
    }

    [Theory]

    // A long is wrong below its entry, and done above it.
    [InlineData(ShadowSide.Long, "100", "95", "110", true)]
    [InlineData(ShadowSide.Long, "100", "100", "110", false)]
    [InlineData(ShadowSide.Long, "100", "105", "110", false)]
    [InlineData(ShadowSide.Long, "100", "95", "100", false)]
    [InlineData(ShadowSide.Long, "100", "95", "90", false)]

    // A short the other way.
    [InlineData(ShadowSide.Short, "100", "105", "90", true)]
    [InlineData(ShadowSide.Short, "100", "95", "90", false)]
    [InlineData(ShadowSide.Short, "100", "105", "110", false)]
    [InlineData(ShadowSide.Short, "100", "100", "90", false)]
    public void The_stop_loss_and_the_take_profit_are_on_the_correct_sides_of_the_entry_or_there_is_no_trade(
        ShadowSide side, string entry, string stop, string target, bool sound)
    {
        var (entryPrice, stopLoss, takeProfit) = (decimal.Parse(entry), decimal.Parse(stop), decimal.Parse(target));

        Assert.Equal(sound, ShadowTradeRule.IsSound(side, entryPrice, stopLoss, takeProfit));

        // The Risk Guard would have rejected such a plan (FR-RSK-4), so its shadow trade has
        // ended as it is written, with no result, and is followed over no candle.
        var trade = Trade(side, priceOfEntry: entryPrice, stopLoss, takeProfit);

        Assert.Equal(sound ? null : ShadowOutcome.Rejected, trade.Outcome);
        Assert.Equal(sound ? null : Close.AddSeconds(40), trade.EvaluatedAt);
        Assert.Null(trade.ResultInR);

        if (!sound)
        {
            Assert.Throws<ArgumentException>(() => ShadowTradeRule.Of(side, entryPrice, resting: false, stopLoss, takeProfit, [], Fee));
        }
    }

    [Fact]
    public void A_shadow_trade_is_written_with_the_prices_of_its_plan_and_with_no_outcome()
    {
        // An entry at the market is entered at the close of the candle that the plan was made on.
        var atMarket = Trade(ShadowSide.Long, priceOfEntry: null, stopLoss: 59000m, takeProfit: 64000m, priceAtClose: 61310m);

        Assert.Equal((61310m, false, 59000m, 64000m), (atMarket.EntryPrice, atMarket.Resting, atMarket.StopLoss, atMarket.TakeProfit));
        Assert.Equal((ShadowSide.Long, TradingMode.Demo, Market.Spot, "BTCUSDT", Timeframe.OneHour), (atMarket.Side, atMarket.Mode, atMarket.Market, atMarket.Symbol, atMarket.Timeframe));
        Assert.Null(atMarket.Outcome);
        Assert.Null(atMarket.ResultInR);
        Assert.Null(atMarket.ExitedAt);
        Assert.Null(atMarket.EvaluatedAt);

        // The first candle after it is the first that says anything of it: it is looked at when that has closed.
        Assert.Equal((Close, Close.AddHours(1)), (atMarket.ClosedAt, atMarket.DueAt));

        // An entry at a price is entered at that price, where it rests.
        var atPrice = Trade(ShadowSide.Long, priceOfEntry: 60500m, stopLoss: 59000m, takeProfit: 64000m, priceAtClose: 61310m);

        Assert.Equal((60500m, true), (atPrice.EntryPrice, atPrice.Resting));
    }

    [Fact]
    public void A_result_is_rounded_as_the_result_of_a_position_is_and_one_out_of_the_range_of_a_number_is_not_said()
    {
        // A third of what was risked, to 12 decimal places.
        Assert.Equal(0.333333333333m, ShadowTradeRule.ResultInR(ShadowSide.Long, entryPrice: 100m, exitPrice: 101m, stopLoss: 97m, NoFee));

        // A stop-loss next to the entry risks next to nothing, so the result is very many R:
        // more than a number holds. Nothing is said, and nothing fails.
        Assert.Null(ShadowTradeRule.ResultInR(
            ShadowSide.Long, entryPrice: 1m, exitPrice: 100m, stopLoss: 0.999999999999999999999999999m, NoFee));

        // Such a trade ends all the same, with how it ended and no result.
        var result = ShadowTradeRule.Of(
            ShadowSide.Long, 1m, resting: false, stopLoss: 0.999999999999999999999999999m, takeProfit: 100m, [Candle(1, low: 1m, high: 100m)], NoFee);

        Assert.Equal(new ShadowResult(ShadowOutcome.TakeProfit, null, Close.AddHours(1)), result);
    }

    private static ShadowResult? Long(IReadOnlyList<ExchangeCandle> later, decimal feeRate) =>
        ShadowTradeRule.Of(ShadowSide.Long, Entry, resting: false, Stop, Target, later, feeRate);

    /// <summary>The candle of an hour, the given number of hours after the close of the plan's candle.</summary>
    private static ExchangeCandle Candle(int index, decimal low, decimal high, decimal? close = null) =>
        new(Close.AddHours(index - 1), Close.AddHours(index), Open: (low + high) / 2m, high, low, close ?? (low + high) / 2m, Volume: 10m);

    private static ShadowTrade Trade(
        ShadowSide side, decimal? priceOfEntry, decimal stopLoss, decimal takeProfit, decimal priceAtClose = 100m) =>
        ShadowTrade.Of(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), TradingMode.Demo, Market.Spot, "BTCUSDT",
            Timeframe.OneHour, Close, side, priceOfEntry, priceAtClose, stopLoss, takeProfit, recordedAt: Close.AddSeconds(40));
}
