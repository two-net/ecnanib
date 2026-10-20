using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Strategies.Rules;

/// <summary>The rule primitives at their boundaries: bias, pullbacks, triggers, breakouts and retests, targets, and every Appendix A filter.</summary>
public class RuleTests
{
    private static readonly DateTimeOffset Saturday = new(2026, 1, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BiasNeedsThePriceSideAndTheMatchingStructure()
    {
        var rising = Build(CandleInterval.OneDay, Monday, Zigzag(100, 4, 2, 2m, 1m, 8));

        Assert.Equal(BiasDirection.Long, HtfBias.Bias(rising, rising[^1].Close, 10, 2, out var ema, out var structure));
        Assert.Equal(SwingStructure.HigherHighsHigherLows, structure);
        Assert.True(ema < rising[^1].Close);
        Assert.Equal(BiasDirection.None, HtfBias.Bias(rising, ema!.Value - 1, 10, 2, out _, out _));
        Assert.Equal(BiasDirection.Short, HtfBias.Bias(Mirror(rising, 120m), 240m - rising[^1].Close, 10, 2, out _, out _));
        Assert.Equal(BiasDirection.None, HtfBias.Bias(rising, rising[^1].Close, 100, 2, out _, out _));

        Assert.True(HtfBias.Agrees(rising, rising[^1].Close, 10, 2, Direction.Long, out _, out _));
        Assert.False(HtfBias.Agrees(rising, rising[^1].Close, 10, 2, Direction.Short, out _, out _));
    }

    [Fact]
    public void RegimeNeedsARisingEmaPriceAboveItAndWeeklyStructure()
    {
        var daily = Build(CandleInterval.OneDay, Monday, Zigzag(100, 6, 2, 2m, 1m, 12));
        var weekly = Build(CandleInterval.OneWeek, Monday, Zigzag(100, 4, 2, 2m, 2m, 5));

        Assert.Equal(BiasDirection.Long, HtfBias.Regime(daily, weekly, 20, 5, 2, out _, out var structure));
        Assert.Equal(SwingStructure.HigherHighsHigherLows, structure);
        Assert.Equal(BiasDirection.Short, HtfBias.Regime(Mirror(daily, 150m), Mirror(weekly, 150m), 20, 5, 2, out _, out _));
        Assert.Equal(BiasDirection.None, HtfBias.Regime(daily, weekly.Take(3).ToList(), 20, 5, 2, out _, out _));
        Assert.Equal(BiasDirection.None, HtfBias.Regime(daily, weekly, 200, 5, 2, out _, out _));
    }

    [Fact]
    public void PullbacksTouchWithinToleranceAndHold()
    {
        var candles = Build(CandleInterval.OneHour, Monday,
        [
            new Bar(100, 101, 99, 100), new Bar(100, 101, 99, 100), new Bar(100, 100.5m, 98.1m, 99), new Bar(99, 100, 98.5m, 99.5m),
        ]);
        var level = LevelSeries.Constant("level", 98m, candles.Count);

        var pullback = Pullbacks.Find(candles, [level], 2, 0.2m, Direction.Long);

        Assert.NotNull(pullback);
        Assert.Equal(("level", 98m, 98.1m), (pullback.Level, pullback.LevelPrice, pullback.Extreme));
        Assert.Null(Pullbacks.Find(candles, [level], 2, 0.05m, Direction.Long));
        Assert.Null(Pullbacks.Find(candles, [LevelSeries.Constant("level", 99.2m, candles.Count)], 2, 0.1m, Direction.Long));
        Assert.Equal("second", Pullbacks.Find(candles, [LevelSeries.Constant("first", 90m, 4), LevelSeries.Constant("second", 98m, 4)], 2, 0.2m, Direction.Long)!.Level);

        var mirrored = Pullbacks.Find(Mirror(candles, 100m), [LevelSeries.Constant("level", 102m, 4)], 2, 0.2m, Direction.Short);
        Assert.Equal(101.9m, mirrored!.Extreme);
    }

    [Fact]
    public void TriggersCompareClosesOpensAndPriorExtremes()
    {
        var prior = new Candle("X", CandleInterval.OneHour, Monday, Monday.AddHours(1), 100, 102, 99, 101, 100, 10_100, 1, true);
        var up = prior with { Open = 101, High = 103, Low = 100.5m, Close = 102.5m, Volume = 150 };
        var inside = prior with { Open = 101, High = 101.8m, Low = 100.5m, Close = 101.5m };
        var down = prior with { Open = 101, High = 101.5m, Low = 99, Close = 99.5m };

        Assert.True(Triggers.ClosesInDirection(up, prior, Direction.Long));
        Assert.True(Triggers.ClosesInDirection(inside, prior, Direction.Long));
        Assert.False(Triggers.ClosesInDirection(down, prior, Direction.Long));
        Assert.True(Triggers.ClosesInDirection(down, prior, Direction.Short));
        Assert.True(Triggers.ClosesBeyondPrior(up, prior, Direction.Long));
        Assert.False(Triggers.ClosesBeyondPrior(inside, prior, Direction.Long));
        Assert.True(Triggers.HoldsLevel(up, 101m, Direction.Long));
        Assert.False(Triggers.HoldsLevel(down, 101m, Direction.Long));

        var candles = Build(CandleInterval.OneHour, Monday, [new Bar(1, 2, 0, 1, 100), new Bar(1, 2, 0, 1, 100), new Bar(1, 2, 0, 1, 150)]);
        Assert.Equal(1.5m, Triggers.VolumeRatio(candles, 2, 2));
        Assert.Null(Triggers.VolumeRatio(candles, 1, 2));
    }

    [Fact]
    public void BreakoutsNeedAValidBoxAndRetestsMustHold()
    {
        var bars = Box(110, 100, 30, 100);
        bars.Add(new Bar(105, 116, 104, 115, 200));  // breakout on 2× volume
        bars.Add(new Bar(115, 115.5m, 109.8m, 111, 100)); // retest touches the top
        bars.Add(new Bar(111, 114, 110.5m, 113, 100)); // holds
        var candles = Build(CandleInterval.OneHour, Monday, bars);

        var breakout = Breakouts.Find(candles, 1m, Direction.Long, 24, 20m, lastIndex: candles.Count - 2, maxAge: 5, volumePeriod: 20, volumeMultiple: 1.2m);
        Assert.NotNull(breakout);
        Assert.Equal((30, 109.5m), (breakout.Index, breakout.Boundary));
        Assert.Equal(9m, breakout.Box.Height);

        var retest = Breakouts.Retest(candles, breakout, 0.5m, Direction.Long);
        Assert.NotNull(retest);
        Assert.Equal(109.8m, retest.Extreme);

        Assert.Null(Breakouts.Find(candles, 1m, Direction.Long, 24, 5m, candles.Count - 2, 5, 20, 1.2m));   // box too tall
        Assert.Null(Breakouts.Find(candles, 1m, Direction.Long, 24, 20m, candles.Count - 2, 5, 20, 3m));    // not enough volume
        Assert.Null(Breakouts.Find(candles, 1m, Direction.Short, 24, 20m, candles.Count - 2, 5, null, 0m));  // no downside break
        Assert.Null(Breakouts.Retest(candles.Take(31).ToList(), breakout, 0.5m, Direction.Long));            // nothing after the breakout
        Assert.Null(Breakouts.Retest(candles, breakout, 0.05m, Direction.Long));                             // never touched within tolerance

        var swings = Swings.Find(candles, 2);
        Assert.Equal(116m, Assert.Single(swings, p => p.Kind == SwingKind.High).Price);
        Assert.Equal(116m, Breakouts.BreakoutLevel(swings, 120m, Direction.Long));
        Assert.Null(Breakouts.BreakoutLevel(swings, 113m, Direction.Long));
    }

    [Fact]
    public void TargetsUseThePriorSwingOrTheFallback()
    {
        var points = new List<SwingPoint> { new(1, 120m, SwingKind.High), new(3, 110m, SwingKind.High), new(5, 90m, SwingKind.Low) };

        Assert.Equal(110m, Targets.PriorSwing(points, 100m, Direction.Long));
        Assert.Equal(90m, Targets.PriorSwing(points, 100m, Direction.Short));
        Assert.Null(Targets.PriorSwing(points, 130m, Direction.Long));
    }

    [Fact]
    public void SessionWindowsAreHalfOpenAndMayWrapMidnight()
    {
        var at = (int hour, int minute) => new DateTimeOffset(2026, 1, 6, hour, minute, 0, TimeSpan.Zero);

        Assert.False(Filters.Session(at(13, 0), 13, 20).Blocks);
        Assert.True(Filters.Session(at(20, 0), 13, 20).Blocks);
        Assert.True(Filters.Session(at(12, 45), 13, 20).Blocks);
        Assert.False(Filters.Session(at(23, 0), 22, 2).Blocks);
        Assert.False(Filters.Session(at(1, 59), 22, 2).Blocks);
        Assert.True(Filters.Session(at(2, 0), 22, 2).Blocks);
        Assert.False(Filters.Session(at(5, 0), 9, 9).Blocks);
        Assert.Equal("session: 14:00 UTC within 13:00-20:00 (passed)", Filters.Session(at(14, 0), 13, 20).ToString());
    }

    [Fact]
    public void WindowsWeekendAndFundingFilters()
    {
        var weekend = new EntryWindow { Id = 1, Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Friday, StartTime = new TimeOnly(21, 0), EndDay = DayOfWeek.Monday, EndTime = new TimeOnly(1, 0) };
        var oneOff = new EntryWindow { Id = 2, Kind = EntryWindowKind.OneOff, StartAt = Saturday, EndAt = Saturday.AddHours(2) };

        Assert.Equal("noEntryWindows: none active (passed)", Filters.NoEntryWindows([oneOff], Saturday.AddHours(2)).ToString());
        Assert.Equal("noEntryWindows: recurring Friday 21:00 → Monday 01:00 (blocked)", Filters.NoEntryWindows([weekend, oneOff], Saturday).ToString());
        Assert.Equal("noEntryWindows: one-off 2026-01-10T10:00Z → 2026-01-10T12:00Z (blocked)", Filters.NoEntryWindows([oneOff], Saturday).ToString());

        var saturday = Filters.Weekend(Saturday, 0.5m);
        Assert.Equal((FilterVerdict.Informational, 0.5m), (saturday.Verdict, saturday.SizeMultiplier));
        Assert.Equal(1m, Filters.Weekend(Monday, 0.5m).SizeMultiplier);

        var mark = new MarkPrice("ETHUSDT", 100m, null, 0.0012m, Saturday, Saturday);
        Assert.True(Filters.FundingRate(mark, 0.001m, Direction.Long).Blocks);
        Assert.False(Filters.FundingRate(mark, 0.001m, Direction.Short).Blocks);
        Assert.True(Filters.FundingRate(mark with { FundingRate = -0.0012m }, 0.001m, Direction.Short).Blocks);
        Assert.Equal(FilterVerdict.Informational, Filters.FundingRate(null, 0.001m, Direction.Long).Verdict);

        var projected = Filters.ProjectedFunding(mark, 18, TimeSpan.FromHours(4), Direction.Long, out var rate);
        Assert.Equal(0.0108m, rate);
        Assert.Equal("projectedFunding: 1.08% over 18 candles (longs pay) (info)", projected.ToString());
        Assert.Contains("shorts earn", Filters.ProjectedFunding(mark, 18, TimeSpan.FromHours(4), Direction.Short, out _).Detail);
    }

    [Fact]
    public void ExtensionLiquidityAndBtcRegimeFilters()
    {
        Assert.True(Filters.Extension(120m, 100m, 15m, Direction.Long, out var percent).Blocks);
        Assert.Equal(20m, percent);
        Assert.False(Filters.Extension(110m, 100m, 15m, Direction.Long, out _).Blocks);
        Assert.False(Filters.Extension(120m, 100m, 15m, Direction.Short, out _).Blocks);

        var thin = Build(CandleInterval.OneDay, Monday, Enumerable.Range(0, 21).Select(_ => new Bar(100, 101, 99, 100, 10)));
        Assert.True(Filters.Liquidity("SOLUSDT", thin, 20, 20_000_000m, out var average).Blocks);
        Assert.Equal(1000m, average);
        Assert.False(Filters.Liquidity("SOLUSDT", thin, 20, 500m, out _).Blocks);
        Assert.Equal(FilterVerdict.Informational, Filters.Liquidity("ETHUSDT", thin, 20, 20_000_000m, out _).Verdict);
        Assert.True(Filters.Liquidity("SOLUSDT", thin.Take(5).ToList(), 20, 1m, out _).Blocks);

        var bullish = Build(CandleInterval.OneWeek, Monday, Zigzag(100, 4, 2, 5m, 2m, 6), "BTCUSDT");
        Assert.False(Filters.BtcRegime("ETHUSDT", bullish, 10, 2, Direction.Long, out var regime, out _).Blocks);
        Assert.Equal(1, regime);
        Assert.True(Filters.BtcRegime("ETHUSDT", bullish, 10, 2, Direction.Short, out _, out _).Blocks);
        Assert.True(Filters.BtcRegime("ETHUSDT", Mirror(bullish, 150m), 10, 2, Direction.Long, out regime, out _).Blocks);
        Assert.Equal(-1, regime);
        Assert.True(Filters.BtcRegime("ETHUSDT", [], 10, 2, Direction.Long, out _, out _).Blocks);
        Assert.Equal("btcRegime: n/a (BTC) (info)", Filters.BtcRegime("BTCUSDT", [], 10, 2, Direction.Long, out _, out _).ToString());
    }
}
