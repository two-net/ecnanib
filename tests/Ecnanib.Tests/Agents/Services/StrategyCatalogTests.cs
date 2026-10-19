using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Agents.Services;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>Appendix A as a catalog: every timeframe has a strategy (FR-AGT-03), every entry a version (FR-STR-06) and bounded parameters (FR-STR-04).</summary>
public class StrategyCatalogTests
{
    private static StrategyCatalog Catalog(int entryTimeout = 1) => new(Options.Create(new AgentsOptions { DefaultEntryTimeoutCandles = entryTimeout }));

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
        Assert.Equal(7, catalog.All.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog.All, s => Assert.Equal(StrategyCatalog.CurrentVersion, s.Version));
        Assert.All(catalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Summary)));
    }

    [Fact]
    public void FindIsExactAndTolerantOfWhitespace()
    {
        var catalog = Catalog();

        Assert.Equal(CandleInterval.OneHour, catalog.Find(" trend-pullback ")!.Timeframe);
        Assert.Null(catalog.Find("Trend-Pullback"));
        Assert.Null(catalog.Find(null));
        Assert.Null(catalog.Find(""));
    }

    [Fact]
    public void DefaultsAreWithinBoundsAndTheEntryTimeoutFollowsTheConfiguration()
    {
        var strategy = Catalog(entryTimeout: 3).Find("ema-pullback")!;

        Assert.All(strategy.Parameters, p => Assert.Null(p.Validate(p.Default)));
        Assert.Equal("3", strategy.Parameter(StrategyParameters.EntryTimeoutCandles)!.DefaultText);
        Assert.Equal("14", strategy.Parameter(StrategyParameters.AtrPeriod)!.DefaultText);
        Assert.Equal("0.33", strategy.Parameter(StrategyParameters.PartialTakeProfitFraction)!.DefaultText);
        Assert.Equal("0.5", Catalog().Find("intraday-trend-pullback")!.Parameter(StrategyParameters.PartialTakeProfitFraction)!.DefaultText);
        Assert.Equal("swing", strategy.Parameter(StrategyParameters.TrailingMethod)!.DefaultText);
        Assert.Equal("false", strategy.Parameter(StrategyParameters.MarketFallback)!.DefaultText);
        Assert.Empty(strategy.Validate(new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void ValidationRejectsUnknownNamesOutOfRangeValuesAndAFastEmaSlowerThanTheSlowOne()
    {
        var strategy = Catalog().Find("trend-pullback")!;

        var unknown = strategy.Validate(new Dictionary<string, JsonElement> { ["bogus"] = JsonSerializer.SerializeToElement(1) });
        Assert.Equal("parameters.bogus", Assert.Single(unknown).Field);

        var range = strategy.Validate(new Dictionary<string, JsonElement> { [StrategyParameters.AtrPeriod] = JsonSerializer.SerializeToElement(1) });
        Assert.Contains("between 2 and 200", Assert.Single(range).Message);

        var kind = strategy.Validate(new Dictionary<string, JsonElement> { [StrategyParameters.TrailingMethod] = JsonSerializer.SerializeToElement("vwap") });
        Assert.Contains("one of swing, ema", Assert.Single(kind).Message);

        var crossed = strategy.Validate(new Dictionary<string, JsonElement> { [StrategyParameters.EmaFastPeriod] = JsonSerializer.SerializeToElement(60) });
        Assert.Contains("shorter than the slow one", Assert.Single(crossed).Message);
    }

    [Theory]
    [InlineData(StrategyParameters.AtrPeriod, "21", true, "21")]
    [InlineData(StrategyParameters.AtrPeriod, "", true, "14")]
    [InlineData(StrategyParameters.AtrPeriod, "abc", false, null)]
    [InlineData(StrategyParameters.AtrPeriod, "500", false, null)]
    [InlineData(StrategyParameters.StopBufferAtr, "1.5", true, "1.5")]
    [InlineData(StrategyParameters.StopBufferAtr, "x", false, null)]
    [InlineData(StrategyParameters.TrailingMethod, "ema", true, "ema")]
    [InlineData(StrategyParameters.TrailingMethod, "vwap", false, null)]
    [InlineData(StrategyParameters.MarketFallback, "true", true, "true")]
    [InlineData(StrategyParameters.MarketFallback, "maybe", false, null)]
    public void ParsesWhatAUserTypes(string name, string text, bool ok, string? expected)
    {
        var parameter = Catalog().Find("trend-pullback")!.Parameter(name)!;

        var parsed = parameter.TryParse(text, out var value, out var error);

        Assert.Equal(ok, parsed);
        if (ok)
        {
            Assert.Null(error);
            Assert.Equal(expected, StrategyParameter.Format(value));
        }
        else
        {
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void ParametersRoundTripThroughTheStoredJson()
    {
        var values = new Dictionary<string, JsonElement>
        {
            [StrategyParameters.AtrPeriod] = JsonSerializer.SerializeToElement(21),
            [StrategyParameters.TrailingMethod] = JsonSerializer.SerializeToElement("ema"),
        };

        var stored = StrategyParameters.Write(values);
        var read = StrategyParameters.Read(stored);

        Assert.Equal("""{"atrPeriod":21,"trailingMethod":"ema"}""", stored.GetRawText());
        Assert.Equal(21, read[StrategyParameters.AtrPeriod].GetInt32());
        Assert.Empty(StrategyParameters.Read(JsonSerializer.SerializeToElement("not an object")));
    }
}
