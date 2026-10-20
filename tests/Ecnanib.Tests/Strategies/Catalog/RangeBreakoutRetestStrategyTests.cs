using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A.2 Range Breakout–Retest on synthetic 1H candles: a 60-candle box, a breakout on 2× volume, a retest that holds.</summary>
public class RangeBreakoutRetestStrategyTests : StrategyScenarioTests
{
    private protected override IStrategy Strategy { get; } = new RangeBreakoutRetestStrategy(StrategyHarness.Options);

    private protected override StrategyHarness LongScenario()
    {
        var bars = Box(110, 100, 60);
        bars.Add(new Bar(105, 116, 104, 115, 200));
        bars.Add(new Bar(115, 115.5m, 109.8m, 111));
        bars.Add(new Bar(111, 114, 110.5m, 113));
        var harness = new StrategyHarness { Timeframe = CandleInterval.OneHour, Candles = Build(CandleInterval.OneHour, Monday, bars) };
        harness.Higher[CandleInterval.OneDay] = RisingSeries(CandleInterval.OneDay, Monday.AddDays(-120), end: 100m, cycles: 10);
        return harness;
    }

    [Fact]
    public void TheNumbersFollowTheRules()
    {
        var harness = LongScenario();
        var output = harness.Evaluate(Strategy);
        var setup = output.Setup!;
        var atr = Atr.Last(harness.Candles, 14)!.Value;
        var r = 113m - (109.8m - atr);

        Assert.Equal(113m, setup.Entry);
        Assert.Equal(Prices.Round(109.8m - atr), setup.Stop);
        Assert.Equal([Prices.Round(113m + 2m * r), Prices.Round(113m + 3m * r)], setup.Targets);
        Assert.Equal("1H close back inside the range (below 109.5)", setup.Invalidation);
        Assert.Equal(["noEntryWindows: none active (passed)", "weekend: Wednesday: full size (info)"], setup.Filters);
        Assert.Contains("breakout of a 24-candle range at 109.5; retest held", output.Note);
        Assert.Equal(109.5m, Assert.Single(output.Levels, l => l.Name == "rangeTop").Price);
        Assert.Equal(100.5m, Assert.Single(output.Levels, l => l.Name == "rangeBottom").Price);
        Assert.Equal(109.8m, Assert.Single(output.Levels, l => l.Name == "retestExtreme").Price);
        Assert.Equal(2m, output.Indicators["breakoutVolumeRatio"]);
        Assert.Equal(Prices.Round(9m / atr), output.Indicators["rangeHeightAtr"]);
    }

    [Fact]
    public void WithoutVolumeOrWithAFailedRetestThereIsNoSetup()
    {
        var quiet = LongScenario();
        quiet.Candles = quiet.Candles.Select(c => c with { Volume = 100 }).ToList();
        var noVolume = quiet.Evaluate(Strategy);
        Assert.Null(noVolume.Setup);
        Assert.Contains("no long breakout", noVolume.Note);

        var failed = LongScenario();
        var last = failed.Candles[^1];
        failed.Candles = [.. failed.Candles.Take(failed.Candles.Count - 1), last with { Open = 111, Close = 105, Low = 104 }];
        var backInside = failed.Evaluate(Strategy);
        Assert.Null(backInside.Setup);
        Assert.Contains("has not held yet", backInside.Note);
        Assert.Contains(backInside.Levels, l => l.Name == "rangeTop");

        var noRetestYet = LongScenario();
        noRetestYet.Candles = noRetestYet.Candles.Take(61).ToList();
        Assert.Contains("no long breakout", noRetestYet.Evaluate(Strategy).Note);
    }
}
