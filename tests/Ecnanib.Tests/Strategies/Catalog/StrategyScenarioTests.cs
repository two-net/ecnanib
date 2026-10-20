using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Strategies.Catalog;

/// <summary>
/// What every Appendix A strategy must do with its long scenario (FR-STR-01/02/05/07): the mirrored short, a spot agent
/// taking no short, a paused agent getting no setup, insufficient history, a no-entry window, and byte-identical JSON
/// for the same input and for reordered parameters. Each strategy's test class supplies the scenario and its own checks.
/// </summary>
public abstract class StrategyScenarioTests
{
    protected const decimal Pivot = 1000m;

    private protected abstract IStrategy Strategy { get; }

    /// <summary>A harness whose evaluation is a long setup.</summary>
    private protected abstract StrategyHarness LongScenario();

    private protected StrategyOutput Long() => LongScenario().Evaluate(Strategy);

    [Fact]
    public void TheLongScenarioIsALongSetupStampedWithTheStrategy()
    {
        var output = Long();

        Assert.Equal(Strategy.Descriptor.Id, output.StrategyId);
        Assert.Equal(1, output.StrategyVersion);
        Assert.Equal(BiasDirection.Long, output.Bias);
        Assert.NotNull(output.Setup);
        Assert.Equal(PositionSide.Long, output.Setup.Side);
        Assert.Equal(OrderType.Limit, output.Setup.EntryType);
        Assert.True(output.Setup.Stop < output.Setup.Entry, "the stop is below a long entry");
        Assert.All(output.Setup.Targets, t => Assert.True(t > output.Setup.Entry, "targets are above a long entry"));
        Assert.Equal(output.Setup.Targets.OrderBy(t => t), output.Setup.Targets);
        Assert.False(string.IsNullOrWhiteSpace(output.Setup.Invalidation));
        Assert.NotEmpty(output.Setup.Filters);
        Assert.StartsWith("Long:", output.Note);
        Assert.Contains("riskAtr", output.Indicators.Keys);
        Assert.Contains("rewardR", output.Indicators.Keys);
        Assert.Contains("atr", output.Indicators.Keys);
        Assert.Contains(output.Levels, l => l.Name == "emaFast");
        Assert.Contains(output.Levels, l => l.Name == "emaSlow");
    }

    [Fact]
    public void TheMirroredScenarioIsTheShortSetup()
    {
        var harness = LongScenario();
        var @long = harness.Evaluate(Strategy);
        var @short = harness.Mirrored(Pivot).Evaluate(Strategy);

        Assert.Equal(BiasDirection.Short, @short.Bias);
        Assert.NotNull(@short.Setup);
        Assert.Equal(PositionSide.Short, @short.Setup.Side);
        Assert.Equal(Prices.Round(2 * Pivot - @long.Setup!.Entry), @short.Setup.Entry);
        Assert.Equal(Prices.Round(2 * Pivot - @long.Setup.Stop), @short.Setup.Stop);
        Assert.Equal(@long.Setup.Targets.Select(t => Prices.Round(2 * Pivot - t)), @short.Setup.Targets);
        Assert.Equal(@long.Indicators["atr"], @short.Indicators["atr"]);
        Assert.Equal(@long.Indicators["riskAtr"], @short.Indicators["riskAtr"]);
        Assert.Equal(@long.Setup.SizeMultiplier, @short.Setup.SizeMultiplier);
        Assert.Equal(@long.Setup.Filters.Count, @short.Setup.Filters.Count);
        Assert.StartsWith("Short:", @short.Note);
    }

    [Fact]
    public void ASpotAgentReportsTheShortBiasButTakesNoShort()
    {
        var harness = LongScenario().Mirrored(Pivot);
        harness.MarketType = MarketType.Spot;

        var output = harness.Evaluate(Strategy);

        Assert.Equal(BiasDirection.Short, output.Bias);
        Assert.Null(output.Setup);
        Assert.Contains("FR-STR-07", output.Note);
        Assert.NotEmpty(output.Levels);
        Assert.NotEmpty(output.Indicators);
    }

    [Fact]
    public void APausedAgentGetsBiasLevelsAndIndicatorsButNoSetup()
    {
        var harness = LongScenario();
        var running = harness.Evaluate(Strategy);
        harness.EntriesAllowed = false;

        var paused = harness.Evaluate(Strategy);

        Assert.Equal(BiasDirection.Long, paused.Bias);
        Assert.Null(paused.Setup);
        Assert.Contains("paused", paused.Note);
        Assert.Equal(running.Levels, paused.Levels);
        Assert.Equal(running.Indicators, paused.Indicators);
    }

    [Fact]
    public void TooLittleHistoryIsSaidSo()
    {
        var harness = LongScenario();
        harness.Candles = harness.Candles.Take(10).ToList();

        var output = harness.Evaluate(Strategy);

        Assert.Equal(BiasDirection.None, output.Bias);
        Assert.Null(output.Setup);
        Assert.Empty(output.Levels);
        Assert.Empty(output.Indicators);
        Assert.StartsWith($"insufficient history: need ", output.Note);
        Assert.EndsWith($" {Strategy.Descriptor.Timeframe.ToLabel()} candles, have 10", output.Note);

        var missingHtf = LongScenario();
        var interval = Strategy.Descriptor.HigherTimeframes[0];
        missingHtf.Higher.Remove(interval);
        Assert.EndsWith($" {interval.ToLabel()} candles, have 0", missingHtf.Evaluate(Strategy).Note);
    }

    [Fact]
    public void ANoEntryWindowCoveringTheCloseSuppressesTheSetupAndOneEndingAtItDoesNot()
    {
        var harness = LongScenario();
        var at = harness.Candles[^1].OpenTime + harness.Timeframe.ToTimeSpan();
        harness.EntryWindows.Add(new EntryWindow { Id = 1, Kind = EntryWindowKind.OneOff, StartAt = at.AddHours(-1), EndAt = at.AddMinutes(1) });

        var blocked = harness.Evaluate(Strategy);
        Assert.Null(blocked.Setup);
        Assert.Contains("noEntryWindows", blocked.Note);
        Assert.Contains("one-off", blocked.Note);

        harness.EntryWindows[0].EndAt = at;
        Assert.NotNull(harness.Evaluate(Strategy).Setup);

        harness.EntryWindows.Clear();
        var day = at.UtcDateTime.DayOfWeek;
        harness.EntryWindows.Add(new EntryWindow { Id = 2, Kind = EntryWindowKind.Recurring, StartDay = day, StartTime = new TimeOnly(0, 0), EndDay = day, EndTime = new TimeOnly(23, 59) });
        Assert.Contains("recurring", harness.Evaluate(Strategy).Note);
    }

    [Fact]
    public void TheSameInputGivesTheSameJsonWhateverTheParameterOrder()
    {
        var harness = LongScenario();
        var first = harness.EvaluateJson(Strategy);
        var second = harness.EvaluateJson(Strategy);
        Assert.Equal(first, second);

        var reversed = LongScenario();
        var descriptor = Strategy.Descriptor;
        foreach (var parameter in descriptor.Parameters.Reverse())
            reversed.Overrides[parameter.Name] = reversed.Overrides.TryGetValue(parameter.Name, out var value) ? value : parameter.Default;
        var context = reversed.Context(Strategy);
        var shuffled = new StrategyEngine([Strategy]).Evaluate(context with { Parameters = context.Parameters.Reverse().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) });
        Assert.Equal(first, JsonSerializer.Serialize(shuffled, StrategyHarness.Json));
        Assert.Contains("\"strategyId\":\"" + descriptor.Id + "\"", first);
    }

    [Fact]
    public void AStopTooFarForTheParameterIsRejected()
    {
        var harness = LongScenario().With(SpecificParameters.MaxStopDistanceAtr, 0.5m);

        var output = harness.Evaluate(Strategy);

        Assert.Null(output.Setup);
        Assert.Contains("stop too far", output.Note);
        Assert.Contains("riskAtr", output.Indicators.Keys);
    }

    /// <summary>A rising zigzag (six up, three down per cycle, strict swings for a lookback of three) of closed candles at <paramref name="interval"/> ending around <paramref name="end"/>.</summary>
    protected static List<Candle> RisingSeries(CandleInterval interval, DateTimeOffset start, decimal end, int cycles, decimal upStep = 2m, decimal downStep = 2m, string symbol = "ETHUSDT") =>
        Build(interval, start, Zigzag(end - cycles * (6 * upStep - 3 * downStep), 6, 3, upStep, downStep, cycles), symbol);
}
