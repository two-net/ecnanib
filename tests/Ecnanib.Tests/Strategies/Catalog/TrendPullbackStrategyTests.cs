using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A.2 Trend Pullback on synthetic 1H candles: daily bias, pullback to EMA20, a close in the trend direction, the weekend multiplier.</summary>
public class TrendPullbackStrategyTests : StrategyScenarioTests
{
    private protected override IStrategy Strategy { get; } = new TrendPullbackStrategy(StrategyHarness.Options);

    /// <summary>The trigger (candle 94 of 94) closes Thursday 22:00 UTC.</summary>
    private protected override StrategyHarness LongScenario() => Scenario(Monday);

    private static StrategyHarness Scenario(DateTimeOffset start)
    {
        var harness = new StrategyHarness
        {
            Timeframe = CandleInterval.OneHour,
            Candles = Build(CandleInterval.OneHour, start, TrendPullbackTrigger(start: 100m, cycles: 10, emaPeriod: 20, pullbackCandles: 3, pullbackStep: 0.5m, triggerVolume: 100m)),
        };
        harness.Higher[CandleInterval.OneDay] = RisingSeries(CandleInterval.OneDay, Monday.AddDays(-120), end: 110m, cycles: 10);
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
        var extreme = candles.Skip(candles.Count - 6).Min(c => c.Low);
        var entry = candles[^1].Close;
        var r = entry - (extreme - atr);
        var dailyEma = Ema.Last(harness.Higher[CandleInterval.OneDay], 20)!.Value;

        Assert.Equal(Prices.Round(extreme - atr), setup.Stop);
        // The prior swing high is closer than 2R, so the final target is the typical 3R.
        Assert.Equal([Prices.Round(entry + 2m * r), Prices.Round(entry + 3m * r)], setup.Targets);
        Assert.Equal($"1H close below {Prices.Format(extreme)} (pullback swing) or daily close below EMA20 ({Prices.Format(dailyEma)})", setup.Invalidation);
        Assert.Equal(["noEntryWindows: none active (passed)", "weekend: Thursday: full size (info)"], setup.Filters);
        Assert.Equal(1m, setup.SizeMultiplier);
        Assert.Contains(output.Levels, l => l.Name == "swingHigh");
        Assert.Equal(Assert.Single(output.Levels, l => l.Name == "swingLow").Price, Assert.Single(output.Levels, l => l.Name == "trailLong").Price);
        Assert.EndsWith("final target at the 3R", output.Note);
        Assert.Equal(3m, output.Indicators["rewardR"]);
        Assert.Equal(Prices.Round(dailyEma), output.Indicators["htfEma"]);
    }

    [Fact]
    public void TheWeekendHalvesTheSizeAndAnEmaTrailIsReportedWhenChosen()
    {
        var saturday = Scenario(Monday.AddHours(48)); // the trigger closes Saturday 22:00
        var output = saturday.With(StrategyParameters.TrailingMethod, "ema").Evaluate(Strategy);

        Assert.NotNull(output.Setup);
        Assert.Equal(0.5m, output.Setup.SizeMultiplier);
        Assert.Contains("weekend: Saturday: size × 0.5 (info)", output.Setup.Filters);
        Assert.Equal(output.Indicators["emaFast"], Assert.Single(output.Levels, l => l.Name == "trailLong").Price);
        Assert.Equal(output.Indicators["emaFast"], Assert.Single(output.Levels, l => l.Name == "trailShort").Price);
    }

    [Fact]
    public void AClosingDownCandleIsNoTrigger()
    {
        var harness = LongScenario();
        var last = harness.Candles[^1];
        harness.Candles = [.. harness.Candles.Take(harness.Candles.Count - 1), last with { Close = last.Open - 0.1m, Low = last.Open - 0.5m }];

        var output = harness.Evaluate(Strategy);

        Assert.Null(output.Setup);
        Assert.Contains("is not a long close", output.Note);
    }
}
