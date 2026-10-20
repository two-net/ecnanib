using System.Text.RegularExpressions;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>Appendix A as a catalog: every timeframe has a strategy (FR-AGT-03), every entry a version (FR-STR-06), bounded parameters (FR-STR-04) and the higher timeframes it needs (FR-STR-03).</summary>
public partial class StrategyCatalogTests
{
    internal static IReadOnlyList<IStrategy> Strategies(int entryTimeout = 1)
    {
        var options = Options.Create(new AgentsOptions { DefaultEntryTimeoutCandles = entryTimeout });
        return
        [
            new IntradayTrendPullbackStrategy(options), new TrendPullbackStrategy(options), new RangeBreakoutRetestStrategy(options),
            new SwingPullbackStrategy(options), new ConsolidationBreakoutStrategy(options), new PositionBreakoutStrategy(options), new EmaPullbackStrategy(options),
        ];
    }

    private static StrategyCatalog Catalog(int entryTimeout = 1) => new(Strategies(entryTimeout));

    [Fact]
    public void EveryAgentTimeframeHasAtLeastOneStrategyAndTheWeeklyIntervalNone()
    {
        var catalog = Catalog();

        Assert.Equal(["intraday-trend-pullback"], catalog.ForTimeframe(CandleInterval.FifteenMinutes).Select(s => s.Id));
        Assert.Equal(["trend-pullback", "range-breakout-retest"], catalog.ForTimeframe(CandleInterval.OneHour).Select(s => s.Id));
        Assert.Equal(["swing-pullback", "consolidation-breakout"], catalog.ForTimeframe(CandleInterval.FourHours).Select(s => s.Id));
        Assert.Equal(["position-breakout", "ema-pullback"], catalog.ForTimeframe(CandleInterval.OneDay).Select(s => s.Id));
        Assert.Empty(catalog.ForTimeframe(CandleInterval.OneWeek));
        Assert.Equal(7, catalog.All.Count);
        Assert.All(catalog.All, s => Assert.Equal(1, s.Version));
        Assert.All(catalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Summary)));
    }

    [Fact]
    public void HigherTimeframesAndReferencesFollowFrStr03()
    {
        var catalog = Catalog();

        Assert.Equal([CandleInterval.FourHours], catalog.Find("intraday-trend-pullback")!.HigherTimeframes);
        Assert.Equal([CandleInterval.OneDay], catalog.Find("trend-pullback")!.HigherTimeframes);
        Assert.Equal([CandleInterval.OneDay], catalog.Find("range-breakout-retest")!.HigherTimeframes);
        Assert.Equal([CandleInterval.OneDay, CandleInterval.OneWeek], catalog.Find("swing-pullback")!.HigherTimeframes);
        Assert.Equal([CandleInterval.OneDay, CandleInterval.OneWeek], catalog.Find("consolidation-breakout")!.HigherTimeframes);
        Assert.Equal([CandleInterval.OneWeek], catalog.Find("position-breakout")!.HigherTimeframes);
        Assert.Equal([CandleInterval.OneWeek], catalog.Find("ema-pullback")!.HigherTimeframes);
        foreach (var strategy in catalog.All)
        {
            var btc = strategy.Timeframe is CandleInterval.FourHours or CandleInterval.OneDay;
            Assert.Equal(btc ? [new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek)] : [], strategy.References);
        }
    }

    [Fact]
    public void FindIsExactTolerantOfWhitespaceAndVersioned()
    {
        var catalog = Catalog();

        Assert.Equal(CandleInterval.OneHour, catalog.Find(" trend-pullback ")!.Timeframe);
        Assert.Null(catalog.Find("Trend-Pullback"));
        Assert.Null(catalog.Find(null));
        Assert.Null(catalog.Find(""));
        Assert.Same(catalog.Find("trend-pullback"), catalog.Find("trend-pullback", 1));
        Assert.Null(catalog.Find("trend-pullback", 2));
        Assert.Null(catalog.Find("trend-pullback", 0));
        Assert.Null(catalog.Find(null, 1));
    }

    [Fact]
    public void ParametersAreUniqueCamelCaseWithinBoundsAndTheEntryTimeoutFollowsTheConfiguration()
    {
        foreach (var strategy in Catalog(entryTimeout: 3).All)
        {
            Assert.All(strategy.Parameters, p => Assert.Null(p.Validate(p.Default)));
            Assert.All(strategy.Parameters, p => Assert.Matches(CamelCase(), p.Name));
            Assert.All(strategy.Parameters, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));
            Assert.Equal(strategy.Parameters.Count, strategy.Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal("3", strategy.Parameter(StrategyParameters.EntryTimeoutCandles)!.DefaultText);
            Assert.Equal("14", strategy.Parameter(StrategyParameters.AtrPeriod)!.DefaultText);
            Assert.NotNull(strategy.Parameter(SpecificParameters.SwingLookback));
            Assert.NotNull(strategy.Parameter(SpecificParameters.MaxStopDistanceAtr));
            Assert.NotNull(strategy.Parameter(SpecificParameters.PartialTakeProfitR));
            Assert.Empty(strategy.Validate(new Dictionary<string, System.Text.Json.JsonElement>()));
        }

        Assert.Equal("0.5", Catalog().Find("intraday-trend-pullback")!.Parameter(StrategyParameters.PartialTakeProfitFraction)!.DefaultText);
        Assert.Equal("0.33", Catalog().Find("ema-pullback")!.Parameter(StrategyParameters.PartialTakeProfitFraction)!.DefaultText);
        Assert.Equal("13", Catalog().Find("intraday-trend-pullback")!.Parameter(SpecificParameters.SessionStartHour)!.DefaultText);
        Assert.Equal("42", Catalog().Find("consolidation-breakout")!.Parameter(SpecificParameters.RangeMinCandles)!.DefaultText);
        Assert.Equal("20", Catalog().Find("position-breakout")!.Parameter(SpecificParameters.ConsolidationMinDays)!.DefaultText);
    }

    [Fact]
    public void ADuplicateVersionIsRefused()
    {
        var strategies = Strategies();

        Assert.Throws<InvalidOperationException>(() => new StrategyCatalog([.. strategies, strategies[0]]));
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex CamelCase();
}
