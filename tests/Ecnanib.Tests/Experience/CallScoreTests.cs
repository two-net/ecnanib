using Ecnanib.Experience;
using Ecnanib.Shared;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// For the two analyst roles the measure is the share of non-neutral calls whose direction
/// matched the price change over the next 4 closed candles, minus 50% (FR-EXP-3). See "The calls
/// of the analysts" in docs/experience.md.
/// </summary>
public class CallScoreTests
{
    private static readonly DateTimeOffset Close = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Theory]

    // The price rose, as a bullish call said, and against a bearish one.
    [InlineData(CallDirection.Bullish, "61310", "61310.01", CallOutcome.Matched)]
    [InlineData(CallDirection.Bearish, "61310", "61310.01", CallOutcome.Missed)]

    // It fell.
    [InlineData(CallDirection.Bearish, "61310", "60000", CallOutcome.Matched)]
    [InlineData(CallDirection.Bullish, "61310", "60000", CallOutcome.Missed)]
    public void A_call_matches_when_the_price_changed_in_the_direction_that_was_called(
        CallDirection direction, string price, string laterPrice, CallOutcome outcome)
    {
        Assert.Equal(outcome, CallScore.Of(direction, decimal.Parse(price), decimal.Parse(laterPrice)));
    }

    [Theory]
    [InlineData(CallDirection.Bullish)]
    [InlineData(CallDirection.Bearish)]
    public void A_price_that_did_not_move_matches_no_direction(CallDirection direction)
    {
        // The call said that the price would change, so it is a sample, and one that was not right.
        Assert.Equal(CallOutcome.Missed, CallScore.Of(direction, 61310m, 61310m));
    }

    [Fact]
    public void A_call_is_scored_over_the_next_four_closed_candles()
    {
        Assert.Equal(4, CallScore.CandlesLater);

        // So it is due when the 4th candle after its own has closed, by the length of a candle
        // of its agent: an hour after a call on 15 minutes, and 4 days after one on a day.
        Assert.Equal(Close.AddHours(1), Call(Timeframe.FifteenMinutes)!.DueAt);
        Assert.Equal(Close.AddHours(4), Call(Timeframe.OneHour)!.DueAt);
        Assert.Equal(Close.AddHours(16), Call(Timeframe.FourHours)!.DueAt);
        Assert.Equal(Close.AddDays(4), Call(Timeframe.OneDay)!.DueAt);
    }

    [Fact]
    public void A_call_is_written_with_what_it_said_and_with_no_outcome()
    {
        var (cycle, user, model) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        var call = AnalystCall.Of(
            cycle, user, Role.SentimentAnalyst, model, TradingMode.Live, Market.Futures, "ETHUSDT", Timeframe.FourHours, Close,
            CallDirection.Bearish, 3120.5m);

        Assert.NotNull(call);
        Assert.Equal((cycle, user, model), (call.CycleId, call.UserId, call.ModelId));
        Assert.Equal(
            (Role.SentimentAnalyst, TradingMode.Live, Market.Futures, "ETHUSDT", Timeframe.FourHours),
            (call.Role, call.Mode, call.Market, call.Symbol, call.Timeframe));
        Assert.Equal((Close, CallDirection.Bearish, 3120.5m), (call.ClosedAt, call.Direction, call.Price));

        // Nothing is said of what became of it until the candles after it have closed.
        Assert.Null(call.Outcome);
        Assert.Null(call.LaterPrice);
        Assert.Null(call.ScoredAt);
    }

    [Fact]
    public void A_neutral_call_is_no_sample()
    {
        // It called no direction, so there is nothing to score, and no row.
        Assert.Null(Call(Timeframe.OneHour, direction: null));
    }

    [Theory]
    [InlineData(Role.Strategist)]
    [InlineData(Role.RiskManager)]
    public void Only_an_analyst_calls_a_direction(Role role)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Call(Timeframe.OneHour, role: role));
    }

    private static AnalystCall? Call(
        Timeframe timeframe, CallDirection? direction = CallDirection.Bullish, Role role = Role.MarketAnalyst) =>
        AnalystCall.Of(
            Guid.CreateVersion7(), Guid.CreateVersion7(), role, Guid.CreateVersion7(), TradingMode.Demo, Market.Spot, "BTCUSDT",
            timeframe, Close, direction, 61310m);
}
