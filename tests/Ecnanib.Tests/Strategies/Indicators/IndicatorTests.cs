using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Tests.Strategies.Support;
using static Ecnanib.Tests.Strategies.Support.CandleSeries;

namespace Ecnanib.Tests.Strategies.Indicators;

/// <summary>Hand-computed indicator values: EMA (SMA seed), Wilder ATR, session VWAP, fractal swings and structure, volume averages, range boxes and rounding.</summary>
public class IndicatorTests
{
    private static List<Bar> Closes(params decimal[] closes) => closes.Select(c => new Bar(c, c + 1, c - 1, c)).ToList();

    [Fact]
    public void EmaIsSeededWithTheSimpleAverage()
    {
        var candles = Build(CandleInterval.OneHour, Monday, Closes(10, 11, 12, 13, 14, 15));

        var ema = Ema.Compute(candles, 3);

        Assert.Equal([null, null, 11m, 12m, 13m, 14m], ema);
        Assert.Equal(14m, Ema.Last(candles, 3));
        Assert.True(Ema.IsRising(ema, 2));
        Assert.False(Ema.IsFalling(ema, 2));
        Assert.Equal(2m, Ema.Change(ema, 2));
        Assert.Null(Ema.IsRising(ema, 5));
        Assert.Null(Ema.Last(Build(CandleInterval.OneHour, Monday, Closes(1, 2)), 3));

        var fractional = Ema.Compute(Build(CandleInterval.OneHour, Monday, Closes(1, 2, 4)), 2);
        Assert.Equal(3.16666667m, Prices.Round(fractional[2]!.Value));
    }

    [Fact]
    public void AtrFollowsWilder()
    {
        var candles = Build(CandleInterval.OneHour, Monday,
        [
            new Bar(11, 12, 10, 11), new Bar(11, 13, 11, 12), new Bar(12, 15, 12, 14), new Bar(14, 14, 11, 12),
        ]);

        var atr = Atr.Compute(candles, 2);

        // TR: 2 (from the second candle), 3, 3; seed (2 + 3) / 2 = 2.5; then (2.5 + 3) / 2 = 2.75.
        Assert.Equal([null, null, 2.5m, 2.75m], atr);
        Assert.Equal(2.75m, Atr.Last(candles, 2));
        Assert.Null(Atr.Last(candles, 4));
    }

    [Fact]
    public void SessionVwapResetsAtMidnightUtcAndNeedsVolume()
    {
        var start = Monday.AddHours(22);
        var candles = Build(CandleInterval.OneHour, start,
        [
            new Bar(11, 12, 10, 11, 100), new Bar(13, 14, 12, 13, 300), new Bar(20, 21, 19, 20, 50), new Bar(20, 21, 19, 20, 0),
        ]);

        var vwap = SessionVwap.Compute(candles);

        Assert.Equal(11m, vwap[0]);
        Assert.Equal(12.5m, vwap[1]);
        Assert.Equal(20m, vwap[2]); // a new UTC day
        Assert.Equal(20m, vwap[3]); // zero volume keeps the session's value
        Assert.Null(SessionVwap.Compute(Build(CandleInterval.OneHour, Monday, [new Bar(1, 2, 0, 1, 0)]))[0]);
    }

    [Fact]
    public void SwingsAreFractalsConfirmedLaterAndStructureReadsTheLastTwo()
    {
        var highs = new decimal[] { 1, 3, 2, 4, 3, 5 };
        var lows = new decimal[] { 0, 2, 1, 3, 2, 4 };
        var candles = Build(CandleInterval.OneHour, Monday, highs.Select((h, i) => new Bar(h - 0.5m, h, lows[i], h - 0.5m)));

        var points = Swings.Find(candles, 1);

        Assert.Equal([(1, 3m, SwingKind.High), (2, 1m, SwingKind.Low), (3, 4m, SwingKind.High), (4, 2m, SwingKind.Low)], points.Select(p => (p.Index, p.Price, p.Kind)));
        Assert.Equal(SwingStructure.HigherHighsHigherLows, Swings.Classify(points));
        Assert.Equal(4m, Swings.Last(points, SwingKind.High)!.Price);
        Assert.Equal(1, Swings.Sign(SwingStructure.HigherHighsHigherLows));

        var mirrored = Swings.Find(Mirror(candles, 10m), 1);
        Assert.Equal(SwingStructure.LowerHighsLowerLows, Swings.Classify(mirrored));
        Assert.Equal(SwingStructure.None, Swings.Classify(points.Take(3).ToList()));

        var equal = Build(CandleInterval.OneHour, Monday, [new Bar(1, 1, 0, 1), new Bar(3, 3, 2, 3), new Bar(3, 3, 2, 3), new Bar(1, 1, 0, 1)]);
        Assert.Equal(1, Assert.Single(Swings.Find(equal, 1), p => p.Kind == SwingKind.High).Index);
        Assert.Empty(Swings.Find(candles, 0));
    }

    [Fact]
    public void VolumeAveragesExcludeTheCandleItself()
    {
        var candles = Build(CandleInterval.OneHour, Monday, Enumerable.Range(1, 5).Select(v => new Bar(10, 11, 9, 10, v)));

        Assert.Equal(3m, VolumeAverage.Before(candles, 4, 3));
        Assert.Equal(30m, VolumeAverage.QuoteBefore(candles, 4, 3));
        Assert.Null(VolumeAverage.Before(candles, 2, 3));
        Assert.Null(VolumeAverage.Before(candles, 6, 3));
        Assert.Equal(3m, VolumeAverage.Before(candles, 5, 5));
    }

    [Fact]
    public void RangeBoxesAreTheEnvelopeCappedByAtr()
    {
        var candles = Build(CandleInterval.OneHour, Monday,
        [
            new Bar(10.2m, 10.5m, 10m, 10.3m), new Bar(10.3m, 10.8m, 10.1m, 10.6m), new Bar(10.6m, 10.6m, 9.9m, 10.2m),
            new Bar(10.2m, 10.9m, 10.2m, 10.7m), new Bar(10.7m, 10.7m, 10m, 10.4m), new Bar(10.4m, 11.5m, 10.4m, 11.4m),
        ]);

        var box = Ranges.Box(candles, 5, 5, 4m, 0.3m);

        Assert.NotNull(box);
        Assert.Equal((0, 4, 10.9m, 9.9m, 1.0m), (box.Start, box.End, box.Top, box.Bottom, box.Height));
        Assert.Null(Ranges.Box(candles, 5, 5, 3m, 0.3m));
        Assert.Null(Ranges.Box(candles, 5, 6, 10m, 0.3m));
        Assert.Null(Ranges.Box(candles, 6, 5, 10m, 0.3m));
    }

    [Fact]
    public void PricesRoundToEightDecimalsAndNormaliseTheScale()
    {
        Assert.Equal("100", Prices.Format(100.00m));
        Assert.Equal("1.12345679", Prices.Format(1.123456789m));
        Assert.Equal("-0.00000001", Prices.Format(-0.000000005m));
        Assert.Equal(Prices.Round(100m).ToString(), Prices.Round(100.000m).ToString());
    }
}
