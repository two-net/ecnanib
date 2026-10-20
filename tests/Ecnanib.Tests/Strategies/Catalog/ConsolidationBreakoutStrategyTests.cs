using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A.3 Consolidation Breakout on synthetic 4H candles: a 60-candle box and a close outside it, with and without the optional retest.</summary>
public class ConsolidationBreakoutStrategyTests : StrategyScenarioTests
{
    private protected override IStrategy Strategy { get; } = new ConsolidationBreakoutStrategy(StrategyHarness.Options);

    private protected override StrategyHarness LongScenario()
    {
        var bars = Box(110, 100, 60);
        bars.Add(new Bar(105, 116, 104, 115, 200));
        var harness = new StrategyHarness { Timeframe = CandleInterval.FourHours, Candles = Build(CandleInterval.FourHours, Monday, bars) };
        harness.Higher[CandleInterval.OneDay] = RisingSeries(CandleInterval.OneDay, Monday.AddDays(-200), end: 100m, cycles: 10);
        harness.Higher[CandleInterval.OneWeek] = RisingSeries(CandleInterval.OneWeek, Monday.AddDays(-7 * 80), end: 95m, cycles: 10);
        harness.References[new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek)] = RisingSeries(CandleInterval.OneWeek, Monday.AddDays(-7 * 80), end: 130m, cycles: 10, symbol: "BTCUSDT");
        return harness;
    }

    [Fact]
    public void TheNumbersFollowTheRules()
    {
        var harness = LongScenario();
        var output = harness.Evaluate(Strategy);
        var setup = output.Setup!;
        var atr = Atr.Last(harness.Candles, 14)!.Value;
        var r = 115m - (109.5m - atr);

        Assert.Equal(115m, setup.Entry);
        Assert.Equal(Prices.Round(109.5m - atr), setup.Stop);
        Assert.Equal([Prices.Round(115m + 3m * r)], setup.Targets);
        Assert.Equal("4H close back inside the range (below 109.5)", setup.Invalidation);
        Assert.Equal(4, setup.Filters.Count);
        Assert.Equal("projectedFunding: n/a (info)", setup.Filters[3]); // futures without a mark price
        Assert.Contains("outside a 42-candle range at 109.5", output.Note);
        Assert.Equal(2m, output.Indicators["volumeRatio"]);
        Assert.Equal(109.5m, Assert.Single(output.Levels, l => l.Name == "rangeTop").Price);
    }

    [Fact]
    public void ARequiredRetestWaitsForItAndThenStopsBeyondItsExtreme()
    {
        var waiting = LongScenario().With(SpecificParameters.RequireRetest, true);
        var notYet = waiting.Evaluate(Strategy);
        Assert.Null(notYet.Setup);
        Assert.Contains("no long breakout", notYet.Note);

        var retested = LongScenario().With(SpecificParameters.RequireRetest, true);
        var bars = Box(110, 100, 60);
        bars.Add(new Bar(105, 116, 104, 115, 200));
        bars.Add(new Bar(115, 115.5m, 109.8m, 111));
        bars.Add(new Bar(111, 114, 110.5m, 113));
        retested.Candles = Build(CandleInterval.FourHours, Monday, bars);
        var output = retested.Evaluate(Strategy);
        Assert.NotNull(output.Setup);
        Assert.Equal(113m, output.Setup.Entry);
        Assert.Equal(Prices.Round(109.8m - Atr.Last(retested.Candles, 14)!.Value), output.Setup.Stop);
        Assert.Contains("retest held", output.Note);
    }
}
