using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A.4 Position Breakout on synthetic daily candles: a rising EMA200 regime, a 25-day consolidation and a breakout on volume; extension, liquidity and BTC regime filters.</summary>
public class PositionBreakoutStrategyTests : StrategyScenarioTests
{
    private protected override IStrategy Strategy { get; } = new PositionBreakoutStrategy(StrategyHarness.Options);

    private protected override StrategyHarness LongScenario()
    {
        var bars = Zigzag(100m, 6, 3, 2m, 2m, 25);
        bars.AddRange(Box(255, 245, 25));
        bars.Add(new Bar(250, 261, 249, 260, 200));
        var start = Monday.AddDays(-bars.Count);
        var harness = new StrategyHarness { Timeframe = CandleInterval.OneDay, Candles = Build(CandleInterval.OneDay, start, bars) };
        harness.Higher[CandleInterval.OneWeek] = RisingSeries(CandleInterval.OneWeek, Monday.AddDays(-7 * 80), end: 230m, cycles: 10);
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
        var r = 260m - (245.5m - atr);

        Assert.Equal(260m, setup.Entry);
        Assert.Equal(Prices.Round(245.5m - atr), setup.Stop);
        Assert.Equal([Prices.Round(260m + 3m * r)], setup.Targets);
        Assert.StartsWith("daily close back below 254.5 (consolidation top) or weekly structure break (close below ", setup.Invalidation);
        Assert.Equal(4, setup.Filters.Count);
        Assert.StartsWith("btcRegime: BTC weekly bullish", setup.Filters[0]);
        Assert.StartsWith("extension: ", setup.Filters[1]);
        Assert.EndsWith("% within 15% above EMA (passed)", setup.Filters[1]);
        Assert.Equal("liquidity: n/a (BTC/ETH) (info)", setup.Filters[2]);
        Assert.Equal("noEntryWindows: none active (passed)", setup.Filters[3]);
        Assert.Contains("beyond a 20-day consolidation at 254.5; volume 2× average", output.Note);
        Assert.Equal(1, output.Indicators["weeklyStructure"]);
        Assert.True(output.Indicators["regimeEmaChange"] > 0);
        Assert.Contains(output.Levels, l => l.Name == "regimeEma");
        Assert.Equal(254.5m, Assert.Single(output.Levels, l => l.Name == "rangeTop").Price);
    }

    [Fact]
    public void ThinSymbolsExtendedPriceAndAMissingWeeklyConfirmationBlock()
    {
        var thin = LongScenario();
        thin.Symbol = "SOLUSDT";
        thin.Candles = thin.Candles.Select(c => c with { QuoteVolume = 1000m }).ToList();
        var illiquid = thin.Evaluate(Strategy);
        Assert.Null(illiquid.Setup);
        Assert.Contains("liquidity", illiquid.Note);

        var extended = LongScenario().With(SpecificParameters.MaxExtensionPercent, 1m);
        var tooFar = extended.Evaluate(Strategy);
        Assert.Null(tooFar.Setup);
        Assert.Contains("extension", tooFar.Note);

        var confirmed = LongScenario().With(SpecificParameters.WeeklyCloseConfirmation, true);
        var unconfirmed = confirmed.Evaluate(Strategy);
        Assert.Null(unconfirmed.Setup);
        Assert.Contains("weekly close", unconfirmed.Note);

        var noVolume = LongScenario();
        var last = noVolume.Candles[^1];
        noVolume.Candles = [.. noVolume.Candles.Take(noVolume.Candles.Count - 1), last with { Volume = 100 }];
        Assert.Contains("volume 1× average, below 1.2×", noVolume.Evaluate(Strategy).Note);
    }
}
