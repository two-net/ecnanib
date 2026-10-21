using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Services;

namespace Ecnanib.Tests.Execution.Services;

/// <summary>Appendix A management at candle close (FR-EXE-06): partial first, then breakeven, then the trail; a stop only ever tightens (FR-AI-15).</summary>
public class PositionManagerTests
{
    private static readonly SymbolFilters Filters = new(0.01m, 0.01m, 1_000_000m, 0.001m, 0.001m, 1000m, null, null, null, 5m, null, true);

    private static Trade Trade(PositionSide side = PositionSide.Long, decimal entry = 100m, decimal stop = 95m, decimal size = 1m, decimal? remaining = null) => new()
    {
        Id = Guid.NewGuid(), AgentId = Guid.NewGuid(), EntryDecisionId = Guid.NewGuid(), Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour,
        Side = side, EntryPrice = entry, Size = size, RemainingSize = remaining ?? size, CurrentStop = stop, OpenedAt = DateTimeOffset.UnixEpoch,
    };

    private static StrategyOutput Output(decimal? trailLong = null, decimal? trailShort = null, decimal atr = 2m)
    {
        var levels = new List<PriceLevel>();
        if (trailLong is { } l)
            levels.Add(new PriceLevel("trailLong", l));
        if (trailShort is { } s)
            levels.Add(new PriceLevel("trailShort", s));
        return new StrategyOutput("trend-pullback", 1, BiasDirection.Long, levels, new Dictionary<string, decimal> { ["atr"] = atr }, null, null);
    }

    private static IReadOnlyDictionary<string, JsonElement> Parameters(decimal buffer = 1m) =>
        new Dictionary<string, JsonElement> { [StrategyParameters.StopBufferAtr] = JsonSerializer.SerializeToElement(buffer) };

    [Fact]
    public void NothingMovesBeforeThePartialHasFilled()
    {
        Assert.Null(PositionManager.Advise(Trade(), Output(trailLong: 110m), Parameters(), Filters, 115m));
    }

    [Fact]
    public void AfterThePartialTheStopGoesToBreakevenThenTrailsBehindTheLevelLessTheBuffer()
    {
        var trade = Trade(remaining: 0.6m);

        var breakeven = PositionManager.Advise(trade, Output(), Parameters(), Filters, 104m);
        var trail = PositionManager.Advise(trade, Output(trailLong: 106.567m, atr: 2m), Parameters(buffer: 1m), Filters, 110m);

        Assert.Equal((100m, PositionManager.Breakeven), (breakeven!.Stop, breakeven.Reason));
        // 106.567 − 2 = 104.567, rounded down to the tick.
        Assert.Equal((104.56m, PositionManager.Trail), (trail!.Stop, trail.Reason));
        Assert.Contains("95 → 104.56", trail.Detail);
    }

    [Fact]
    public void AShortMirrorsTheRulesAndRoundsUp()
    {
        var trade = Trade(PositionSide.Short, entry: 100m, stop: 105m, remaining: 0.5m);

        var trail = PositionManager.Advise(trade, Output(trailShort: 94.123m, atr: 1m), Parameters(buffer: 0.5m), Filters, 92m);

        Assert.Equal((94.63m, PositionManager.Trail), (trail!.Stop, trail.Reason));
    }

    [Fact]
    public void TheStopNeverWidensAndNeverSitsAcrossTheLastClose()
    {
        var tightened = Trade(remaining: 0.6m, stop: 108m);

        Assert.Null(PositionManager.Advise(tightened, Output(trailLong: 107m), Parameters(buffer: 0m), Filters, 110m));
        Assert.Null(PositionManager.Advise(Trade(remaining: 0.6m), Output(trailLong: 112m), Parameters(buffer: 0m), Filters, 99m));
        var breakevenOnly = PositionManager.Advise(Trade(remaining: 0.6m), Output(trailLong: 112m), Parameters(buffer: 0m), Filters, 103m);
        Assert.Equal(PositionManager.Breakeven, breakevenOnly!.Reason);
    }

    [Fact]
    public void AnUnmanagedOrUnprotectedTradeIsLeftAlone()
    {
        var unmanaged = Trade(remaining: 0.5m);
        unmanaged.IsUnmanaged = true;
        var unprotected = Trade(remaining: 0.5m);
        unprotected.CurrentStop = null;

        Assert.Null(PositionManager.Advise(unmanaged, Output(trailLong: 110m), Parameters(), Filters, 115m));
        Assert.Null(PositionManager.Advise(unprotected, Output(trailLong: 110m), Parameters(), Filters, 115m));
    }

    [Fact]
    public void TheCommonParametersAreReadWithTheirDefaults()
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            [StrategyParameters.EntryTimeoutCandles] = JsonSerializer.SerializeToElement(3),
            [StrategyParameters.MarketFallback] = JsonSerializer.SerializeToElement(true),
            [StrategyParameters.PartialTakeProfitFraction] = JsonSerializer.SerializeToElement(0.33m),
        };

        Assert.Equal(3, PositionManager.EntryTimeoutCandles(parameters, 1));
        Assert.Equal(1, PositionManager.EntryTimeoutCandles(new Dictionary<string, JsonElement>(), 1));
        Assert.True(PositionManager.MarketFallback(parameters));
        Assert.False(PositionManager.MarketFallback(new Dictionary<string, JsonElement>()));
        Assert.Equal(0.33m, ProtectionUnits.PartialFraction(parameters));
        Assert.Equal(0.5m, ProtectionUnits.PartialFraction(new Dictionary<string, JsonElement>()));
    }
}
