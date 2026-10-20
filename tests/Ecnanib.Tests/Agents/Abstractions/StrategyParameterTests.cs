using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Agents.Abstractions;

/// <summary>Bounded parameters with defaults, parsing what a user types and the stored JSON shape (FR-STR-04), independent of any catalog.</summary>
public class StrategyParameterTests
{
    private static readonly StrategyDescriptor Strategy = new("test", "Test", CandleInterval.OneHour, 1, "A test strategy.",
    [
        StrategyParameter.Integer(StrategyParameters.AtrPeriod, "ATR period", 14, 2, 200),
        StrategyParameter.Integer(StrategyParameters.EmaFastPeriod, "Fast EMA period", 20, 2, 400),
        StrategyParameter.Integer(StrategyParameters.EmaSlowPeriod, "Slow EMA period", 50, 2, 400),
        StrategyParameter.Decimal(StrategyParameters.StopBufferAtr, "Stop buffer (× ATR)", 1.0m, 0m, 5m),
        StrategyParameter.Choice(StrategyParameters.TrailingMethod, "Trailing method", StrategyParameters.TrailingBySwing, StrategyParameters.TrailingBySwing, StrategyParameters.TrailingByEma),
        StrategyParameter.Flag(StrategyParameters.MarketFallback, "Market fallback", false),
    ], [CandleInterval.OneDay], []);

    [Fact]
    public void DefaultsAreWithinBoundsAndResolveFillsTheGaps()
    {
        Assert.All(Strategy.Parameters, p => Assert.Null(p.Validate(p.Default)));
        Assert.Equal("14", Strategy.Parameter(StrategyParameters.AtrPeriod)!.DefaultText);
        Assert.Equal("1.0", Strategy.Parameter(StrategyParameters.StopBufferAtr)!.DefaultText);
        Assert.Equal("swing", Strategy.Parameter(StrategyParameters.TrailingMethod)!.DefaultText);
        Assert.Equal("false", Strategy.Parameter(StrategyParameters.MarketFallback)!.DefaultText);
        Assert.Empty(Strategy.Validate(new Dictionary<string, JsonElement>()));

        var resolved = Strategy.Resolve(new Dictionary<string, JsonElement> { [StrategyParameters.AtrPeriod] = JsonSerializer.SerializeToElement(21) });
        Assert.Equal(6, resolved.Count);
        Assert.Equal(21, resolved[StrategyParameters.AtrPeriod].GetInt32());
        Assert.Equal(50, resolved[StrategyParameters.EmaSlowPeriod].GetInt32());
    }

    [Fact]
    public void ValidationRejectsUnknownNamesOutOfRangeValuesAndAFastEmaSlowerThanTheSlowOne()
    {
        var unknown = Strategy.Validate(new Dictionary<string, JsonElement> { ["bogus"] = JsonSerializer.SerializeToElement(1) });
        Assert.Equal("parameters.bogus", Assert.Single(unknown).Field);

        var range = Strategy.Validate(new Dictionary<string, JsonElement> { [StrategyParameters.AtrPeriod] = JsonSerializer.SerializeToElement(1) });
        Assert.Contains("between 2 and 200", Assert.Single(range).Message);

        var kind = Strategy.Validate(new Dictionary<string, JsonElement> { [StrategyParameters.TrailingMethod] = JsonSerializer.SerializeToElement("vwap") });
        Assert.Contains("one of swing, ema", Assert.Single(kind).Message);

        var crossed = Strategy.Validate(new Dictionary<string, JsonElement> { [StrategyParameters.EmaFastPeriod] = JsonSerializer.SerializeToElement(60) });
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
        var parameter = Strategy.Parameter(name)!;

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
