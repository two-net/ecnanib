using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A.1 on synthetic 15m candles: 4H bias, pullback to EMA20, close beyond the prior high on volume, the session, funding and no-entry filters.</summary>
public class IntradayTrendPullbackStrategyTests : StrategyScenarioTests
{
    /// <summary>Sunday 18:00: the trigger (candle 94 of 94) then closes Monday 17:30 UTC, inside the default session.</summary>
    private static readonly DateTimeOffset Start = Monday.AddHours(-6);

    private protected override IStrategy Strategy { get; } = new IntradayTrendPullbackStrategy(StrategyHarness.Options);

    private protected override StrategyHarness LongScenario()
    {
        var bars = TrendPullbackTrigger(start: 100m, cycles: 10, emaPeriod: 20, pullbackCandles: 3, pullbackStep: 0.5m, triggerVolume: 200m);
        var harness = new StrategyHarness
        {
            Timeframe = CandleInterval.FifteenMinutes,
            Candles = Build(CandleInterval.FifteenMinutes, Start, bars),
            Mark = new MarkPrice("ETHUSDT", 150m, null, 0.0001m, Monday.AddHours(16), Monday.AddHours(15)),
        };
        harness.Higher[CandleInterval.FourHours] = RisingSeries(CandleInterval.FourHours, Monday.AddDays(-30), end: 120m, cycles: 10);
        return harness.With(SpecificParameters.MaxStopDistanceAtr, 6m);
    }

    [Fact]
    public void TheNumbersFollowTheRules()
    {
        var harness = LongScenario();
        var candles = harness.Candles;
        var output = harness.Evaluate(Strategy);
        var setup = output.Setup!;
        var atr = Atr.Last(candles, 14)!.Value;
        var extreme = candles.Skip(candles.Count - 7).Min(c => c.Low);
        var entry = candles[^1].Close;
        var r = entry - (extreme - atr);

        Assert.Equal(Prices.Round(entry), setup.Entry);
        Assert.Equal(Prices.Round(extreme - atr), setup.Stop);
        Assert.Equal([Prices.Round(entry + 1.5m * r), Prices.Round(entry + 2m * r)], setup.Targets);
        Assert.Equal($"15m close below {Prices.Format(extreme)} (pullback extreme) or 4H bias no longer long", setup.Invalidation);
        Assert.Equal(["session: 17:30 UTC within 13:00-20:00 (passed)", "noEntryWindows: none active (passed)", "fundingRate: 0.0001 below 0.001 for longs (passed)"], setup.Filters);
        Assert.Contains(output.Levels, l => l.Name == "swingHigh");
        Assert.Contains(output.Levels, l => l.Name == "trailLong");
        Assert.Equal(1m, setup.SizeMultiplier);
        Assert.Contains("pullback to emaFast held", output.Note);
        Assert.Contains("volume 2× average", output.Note);
        Assert.Equal(2m, output.Indicators["volumeRatio"]);
        Assert.Equal(1, output.Indicators["htfStructure"]);
        Assert.Equal(Prices.Round(extreme), Assert.Single(output.Levels, l => l.Name == "pullbackExtreme").Price);
        Assert.Contains(output.Levels, l => l.Name == "sessionVwap");
        Assert.Contains(output.Levels, l => l.Name == "htfEma");
        Assert.Equal(["htfEma", "emaFast", "emaSlow", "sessionVwap", "breakoutLevel", "pullbackExtreme", "swingHigh", "swingLow", "trailLong", "trailShort"], output.Levels.Select(l => l.Name));
    }

    [Fact]
    public void OutsideTheSessionOrWithoutVolumeOrBeyondTheFundingExtremeThereIsNoSetup()
    {
        var late = LongScenario().With(SpecificParameters.SessionEndHour, 15);
        var blocked = late.Evaluate(Strategy);
        Assert.Null(blocked.Setup);
        Assert.Equal("Long setup blocked by session: 17:30 UTC outside 13:00-15:00", blocked.Note);

        var overnight = LongScenario().With(SpecificParameters.SessionStartHour, 14).With(SpecificParameters.SessionEndHour, 2);
        Assert.NotNull(overnight.Evaluate(Strategy).Setup);

        var quiet = LongScenario().With(SpecificParameters.VolumeMultiple, 3m);
        var noVolume = quiet.Evaluate(Strategy);
        Assert.Null(noVolume.Setup);
        Assert.Contains("volume 2× average, below 3×", noVolume.Note);

        var crowded = LongScenario();
        crowded.Mark = crowded.Mark! with { FundingRate = 0.002m };
        var funding = crowded.Evaluate(Strategy);
        Assert.Null(funding.Setup);
        Assert.Contains("fundingRate", funding.Note);
        // The same positive rate crowds the longs only: the mirrored short passes (the mirror negates the rate, so set it back).
        var shorts = crowded.Mirrored(Pivot);
        shorts.Mark = shorts.Mark! with { FundingRate = 0.002m };
        Assert.NotNull(shorts.Evaluate(Strategy).Setup);

        var spot = LongScenario();
        spot.MarketType = MarketType.Spot;
        spot.Mark = null;
        Assert.Equal(2, spot.Evaluate(Strategy).Setup!.Filters.Count);
    }

    [Fact]
    public void WithoutAPullbackOrAgainstTheBiasThereIsNoSetup()
    {
        var noPullback = LongScenario();
        noPullback.Candles = RisingSeries(CandleInterval.FifteenMinutes, Start, end: 180m, cycles: 11, upStep: 1m, downStep: 1m);
        var output = noPullback.Evaluate(Strategy);
        Assert.Equal(BiasDirection.Long, output.Bias);
        Assert.Null(output.Setup);
        Assert.StartsWith("Long bias; ", output.Note);

        var flat = LongScenario();
        flat.Higher[CandleInterval.FourHours] = Build(CandleInterval.FourHours, Monday.AddDays(-30), Box(150, 140, 60));
        var none = flat.Evaluate(Strategy);
        Assert.Equal(BiasDirection.None, none.Bias);
        Assert.Null(none.Setup);
        Assert.StartsWith("No 4H bias", none.Note);
        Assert.Equal(0, none.Indicators["htfStructure"]);
    }
}
