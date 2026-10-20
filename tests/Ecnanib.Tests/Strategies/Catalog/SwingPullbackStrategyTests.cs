using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A.3 Swing Pullback on synthetic 4H candles: weekly bias with daily agreement, pullback to EMA50, BTC regime, weekend and funding filters.</summary>
public class SwingPullbackStrategyTests : StrategyScenarioTests
{
    private protected override IStrategy Strategy { get; } = new SwingPullbackStrategy(StrategyHarness.Options);

    /// <summary>The trigger (candle 94 of 94) closes Tuesday 16:00 UTC.</summary>
    private protected override StrategyHarness LongScenario()
    {
        var harness = new StrategyHarness
        {
            Timeframe = CandleInterval.FourHours,
            Candles = Build(CandleInterval.FourHours, Monday, TrendPullbackTrigger(start: 100m, cycles: 10, emaPeriod: 50, pullbackCandles: 3, pullbackStep: 0.5m, triggerVolume: 100m)),
            Mark = new MarkPrice("ETHUSDT", 150m, null, 0.0001m, Monday.AddDays(14).AddHours(8), Monday.AddDays(14)),
        };
        harness.Higher[CandleInterval.OneDay] = RisingSeries(CandleInterval.OneDay, Monday.AddDays(-200), end: 110m, cycles: 10);
        harness.Higher[CandleInterval.OneWeek] = RisingSeries(CandleInterval.OneWeek, Monday.AddDays(-7 * 80), end: 110m, cycles: 10);
        harness.References[new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek)] = RisingSeries(CandleInterval.OneWeek, Monday.AddDays(-7 * 80), end: 130m, cycles: 10, symbol: "BTCUSDT");
        return harness.With(SpecificParameters.MaxStopDistanceAtr, 12m);
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
        Assert.StartsWith($"4H close below {Prices.Format(extreme)} (pullback swing) or weekly close below EMA20 (", setup.Invalidation);
        Assert.Equal(4, setup.Filters.Count);
        Assert.StartsWith("btcRegime: BTC weekly bullish", setup.Filters[0]);
        Assert.EndsWith("(passed)", setup.Filters[0]);
        Assert.Equal("noEntryWindows: none active (passed)", setup.Filters[1]);
        Assert.Equal("weekend: Tuesday: full size (info)", setup.Filters[2]);
        Assert.Equal("projectedFunding: 0.09% over 18 candles (longs pay) (info)", setup.Filters[3]);
        Assert.Contains("pullback to emaSlow held", output.Note);
        Assert.Equal(1, output.Indicators["btcRegime"]);
        Assert.Equal(0.0009m, output.Indicators["projectedFundingRate"]);
        Assert.Equal(1, output.Indicators["dailyStructure"]);
        Assert.Contains(output.Levels, l => l.Name == "weeklyEma");
        Assert.Contains(output.Levels, l => l.Name == "weeklySwingLow");
    }

    [Fact]
    public void TheBtcRegimeBlocksAgainstItOrWhenUnavailableAndNotForBtc()
    {
        var bearish = LongScenario();
        var series = new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek);
        bearish.References[series] = Mirror(bearish.References[series], 150m);
        var blocked = bearish.Evaluate(Strategy);
        Assert.Null(blocked.Setup);
        Assert.Contains("btcRegime: BTC weekly bearish", blocked.Note);
        Assert.Equal(-1, blocked.Indicators["btcRegime"]);

        var missing = LongScenario();
        missing.References.Clear();
        var unavailable = missing.Evaluate(Strategy);
        Assert.Null(unavailable.Setup);
        Assert.Contains("BTC weekly series unavailable", unavailable.Note);

        var btc = LongScenario();
        btc.Symbol = "BTCUSDT";
        btc.References.Clear();
        var own = btc.Evaluate(Strategy);
        Assert.NotNull(own.Setup);
        Assert.Equal("btcRegime: n/a (BTC) (info)", own.Setup.Filters[0]);
    }

    [Fact]
    public void WithoutDailyAgreementThereIsNoBias()
    {
        var harness = LongScenario();
        harness.Higher[CandleInterval.OneDay] = Mirror(harness.Higher[CandleInterval.OneDay], 60m);

        var output = harness.Evaluate(Strategy);

        Assert.Equal(BiasDirection.None, output.Bias);
        Assert.StartsWith("No weekly bias with daily agreement", output.Note);
        Assert.Equal(1, output.Indicators["htfStructure"]);
    }
}
