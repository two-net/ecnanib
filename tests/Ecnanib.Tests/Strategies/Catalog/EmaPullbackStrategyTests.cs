using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A.4 EMA Pullback on synthetic daily candles: the EMA200 regime, a pullback to EMA20 and a daily close holding it.</summary>
public class EmaPullbackStrategyTests : StrategyScenarioTests
{
    private protected override IStrategy Strategy { get; } = new EmaPullbackStrategy(StrategyHarness.Options);

    private protected override StrategyHarness LongScenario()
    {
        var bars = TrendPullbackTrigger(start: 100m, cycles: 27, emaPeriod: 20, pullbackCandles: 3, pullbackStep: 0.5m, triggerVolume: 100m);
        var harness = new StrategyHarness { Timeframe = CandleInterval.OneDay, Candles = Build(CandleInterval.OneDay, Monday.AddDays(-bars.Count), bars) };
        harness.Higher[CandleInterval.OneWeek] = RisingSeries(CandleInterval.OneWeek, Monday.AddDays(-7 * 80), end: 170m, cycles: 10);
        harness.References[new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek)] = RisingSeries(CandleInterval.OneWeek, Monday.AddDays(-7 * 80), end: 130m, cycles: 10, symbol: "BTCUSDT");
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

        Assert.Equal(Prices.Round(extreme - atr), setup.Stop);
        Assert.Equal([Prices.Round(entry + 3m * r)], setup.Targets);
        Assert.StartsWith($"daily close below EMA50 (", setup.Invalidation);
        Assert.Contains("weekly structure break", setup.Invalidation);
        Assert.Equal(4, setup.Filters.Count);
        Assert.Contains("pullback to emaFast held", output.Note);
        Assert.Contains(output.Levels, l => l.Name == "regimeEma");
        Assert.Equal(Prices.Round(extreme), Assert.Single(output.Levels, l => l.Name == "pullbackExtreme").Price);
    }

    [Fact]
    public void WithoutTheRegimeThereIsNoBias()
    {
        var harness = LongScenario();
        harness.Higher[CandleInterval.OneWeek] = Build(CandleInterval.OneWeek, Monday.AddDays(-7 * 60), Box(230, 220, 60));

        var output = harness.Evaluate(Strategy);

        Assert.Equal(BiasDirection.None, output.Bias);
        Assert.StartsWith("No daily regime", output.Note);
        Assert.Equal(0, output.Indicators["weeklyStructure"]);
    }
}
