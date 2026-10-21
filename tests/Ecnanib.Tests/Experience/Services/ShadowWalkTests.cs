using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Experience.Services;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Experience.Services;

/// <summary>The shadow outcome walk (FR-EXP-03): fills, stop before target, the horizon, and determinism.</summary>
public class ShadowWalkTests
{
    private static readonly DateTimeOffset Decision = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StaysPendingUntilTheLimitEntryIsTouched()
    {
        var detail = Long(entryTimeout: 3);

        var step = ShadowWalk.Walk(detail, [Candle(1, 101m, 103m, 100.5m, 102m)]);

        Assert.Equal(ShadowResult.Pending, step.Result);
        Assert.Null(step.RMultiple);
        Assert.Null(step.Detail.FilledAt);
        Assert.Equal(1, step.Detail.CandlesSeen);
        Assert.Equal(Decision.AddHours(1), step.Detail.LastCandleOpenTime);
    }

    [Fact]
    public void ExpiresUnfilledOnceTheEntryTimeoutHasPassed()
    {
        var detail = Long(entryTimeout: 2);

        var step = ShadowWalk.Walk(detail, [Candle(1, 101m, 103m, 100.5m, 102m), Candle(2, 102m, 104m, 101m, 103m), Candle(3, 103m, 105m, 90m, 95m)]);

        Assert.Equal(ShadowResult.Expired, step.Result);
        Assert.Equal(ShadowDetail.NotFilled, step.Detail.Resolution);
        Assert.Null(step.RMultiple);
        Assert.Equal(Decision.AddHours(2), step.Detail.ResolvedCandleOpenTime);
    }

    [Fact]
    public void AFillThenAStopEndsAtMinusOneR()
    {
        var detail = Long();

        var step = ShadowWalk.Walk(detail, [Candle(1, 101m, 102m, 99m, 101m), Candle(2, 101m, 103m, 95m, 97m)]);

        Assert.Equal(ShadowResult.HitStop, step.Result);
        Assert.Equal(-1m, step.RMultiple);
        Assert.Equal(ShadowDetail.ByStop, step.Detail.Resolution);
        Assert.Equal(Decision.AddHours(1), step.Detail.FilledAt);
        Assert.Equal(100m, step.Detail.FillPrice);
        Assert.Equal(Decision.AddHours(2), step.Detail.ResolvedCandleOpenTime);
    }

    [Fact]
    public void AFillThenTheTargetEndsAtTheSetupsReward()
    {
        var detail = Long();

        var step = ShadowWalk.Walk(detail, [Candle(1, 101m, 102m, 99m, 101m), Candle(2, 101m, 109m, 100m, 108m)]);

        Assert.Equal(ShadowResult.HitTarget, step.Result);
        Assert.Equal(2m, step.RMultiple);
        Assert.Equal(ShadowDetail.ByTarget, step.Detail.Resolution);
    }

    [Fact]
    public void ACandleTouchingBothStopAndTargetCountsAsTheStopEvenOnTheFillCandle()
    {
        var detail = Long();

        var step = ShadowWalk.Walk(detail, [Candle(1, 101m, 110m, 95m, 105m)]);

        Assert.Equal(ShadowResult.HitStop, step.Result);
        Assert.Equal(-1m, step.RMultiple);
        Assert.Equal(Decision.AddHours(1), step.Detail.FilledAt);
    }

    [Fact]
    public void AMarketEntryFillsAtTheNextOpen()
    {
        var detail = Long(entryType: OrderType.Market);

        var step = ShadowWalk.Walk(detail, [Candle(1, 102m, 103m, 101m, 102m), Candle(2, 102m, 109m, 101m, 108m)]);

        Assert.Equal(ShadowResult.HitTarget, step.Result);
        Assert.Equal(102m, step.Detail.FillPrice);
        // Risk is measured from the fill: (108 - 102) / (102 - 96) = 1.
        Assert.Equal(1m, step.RMultiple);
    }

    [Fact]
    public void AShortSetupIsMirrored()
    {
        var detail = new ShadowDetail { Reason = ShadowDetail.Veto, Side = PositionSide.Short, EntryType = OrderType.Limit, Entry = 100m, Stop = 104m, Target = 92m, DecisionCandleOpenTime = Decision, EntryTimeoutCandles = 1, HorizonCandles = 30 };

        var stop = ShadowWalk.Walk(detail, [Candle(1, 99m, 100.5m, 98m, 99m), Candle(2, 99m, 105m, 98m, 104m)]);
        var target = ShadowWalk.Walk(detail, [Candle(1, 99m, 100.5m, 98m, 99m), Candle(2, 99m, 100m, 91m, 92m)]);

        Assert.Equal(ShadowResult.HitStop, stop.Result);
        Assert.Equal(ShadowResult.HitTarget, target.Result);
        Assert.Equal(2m, target.RMultiple);
    }

    [Fact]
    public void ExpiresAtTheHorizonWithTheMarkToMarketR()
    {
        var detail = Long(horizon: 3);

        var step = ShadowWalk.Walk(detail, [Candle(1, 101m, 102m, 99m, 101m), Candle(2, 101m, 103m, 100m, 102m), Candle(3, 102m, 104m, 101m, 103m), Candle(4, 103m, 109m, 102m, 108m)]);

        Assert.Equal(ShadowResult.Expired, step.Result);
        Assert.Equal(ShadowDetail.Horizon, step.Detail.Resolution);
        Assert.Equal(0.75m, step.RMultiple);
        Assert.Equal(0.75m, step.Detail.MarkR);
        Assert.Equal(Decision.AddHours(3), step.Detail.ResolvedCandleOpenTime);
    }

    [Fact]
    public void OnlyCandlesAfterTheDecisionCountWhateverTheirOrder()
    {
        var detail = Long();
        var candles = new[] { Candle(2, 101m, 109m, 100m, 108m), Candle(-1, 50m, 200m, 10m, 100m), Candle(0, 100m, 100m, 100m, 100m), Candle(1, 101m, 102m, 99m, 101m) };

        var step = ShadowWalk.Walk(detail, candles);

        Assert.Equal(ShadowResult.HitTarget, step.Result);
        Assert.Equal(2, step.Detail.CandlesSeen);
        Assert.Equal(Decision.AddHours(1), step.Detail.FilledAt);
    }

    [Fact]
    public void ReWalkingTheSameCandlesGivesTheSameStepAndLeavesTheStoredDetailAlone()
    {
        var detail = Long();
        var candles = new[] { Candle(1, 101m, 102m, 99m, 101m), Candle(2, 101m, 103m, 100m, 102m) };

        var first = ShadowWalk.Walk(detail, candles);
        var second = ShadowWalk.Walk(first.Detail, candles);

        Assert.Equal(ShadowResult.Pending, first.Result);
        Assert.Equal(first.Detail.ToJson().GetRawText(), second.Detail.ToJson().GetRawText());
        Assert.Null(detail.FilledAt);
        Assert.Equal(0, detail.CandlesSeen);
    }

    [Fact]
    public void ASetupWithTheStopOrTargetOnTheWrongSideIsInvalid()
    {
        var wrongStop = Long();
        wrongStop.Stop = 104m;

        var step = ShadowWalk.Walk(wrongStop, [Candle(1, 101m, 102m, 99m, 101m)]);

        Assert.Equal(ShadowResult.Expired, step.Result);
        Assert.Equal(ShadowDetail.Invalid, step.Detail.Resolution);
    }

    [Fact]
    public void ExpireKeepsTheDetailAndNamesTheResolution()
    {
        var step = ShadowWalk.Expire(Long(), ShadowDetail.Stale);
        var none = ShadowWalk.Expire(null, ShadowDetail.NoDetail);

        Assert.Equal(ShadowResult.Expired, step.Result);
        Assert.Equal(ShadowDetail.Stale, step.Detail.Resolution);
        Assert.Equal(100m, step.Detail.Entry);
        Assert.Equal(ShadowDetail.NoDetail, none.Detail.Resolution);
    }

    [Fact]
    public void DetailRoundTripsThroughJsonAndRejectsWhatCannotBeWalked()
    {
        var detail = Long();
        var parsed = ShadowDetail.Parse(detail.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(detail.Entry, parsed.Entry);
        Assert.Equal(detail.Side, parsed.Side);
        Assert.Equal(detail.DecisionCandleOpenTime, parsed.DecisionCandleOpenTime);
        Assert.Null(ShadowDetail.Parse(null));
        Assert.Null(ShadowDetail.Parse(System.Text.Json.JsonDocument.Parse("[]").RootElement));
        Assert.Null(ShadowDetail.Parse(System.Text.Json.JsonDocument.Parse("""{"entry":0}""").RootElement));
        Assert.Null(ShadowDetail.Parse(System.Text.Json.JsonDocument.Parse("""{"side":"Sideways"}""").RootElement));
    }

    private static ShadowDetail Long(OrderType entryType = OrderType.Limit, int entryTimeout = 1, int horizon = 30) => new()
    {
        Reason = ShadowDetail.Veto,
        Side = PositionSide.Long,
        EntryType = entryType,
        Entry = 100m,
        Stop = 96m,
        Target = 108m,
        DecisionCandleOpenTime = Decision,
        EntryTimeoutCandles = entryTimeout,
        HorizonCandles = horizon,
    };

    private static Candle Candle(int hoursAfterDecision, decimal open, decimal high, decimal low, decimal close)
    {
        var openTime = Decision.AddHours(hoursAfterDecision);
        return new Candle("BTCUSDT", CandleInterval.OneHour, openTime, openTime.AddHours(1).AddMilliseconds(-1), open, high, low, close, 100m, 10_000m, 10, true);
    }
}
