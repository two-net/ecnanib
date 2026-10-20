using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Indicators;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Strategies.Support;

/// <summary>Synthetic candle series whose indicators can be reasoned about: linear legs, zigzags, boxes, and mirrors of all of them.</summary>
internal static class CandleSeries
{
    /// <summary>Monday 2026-01-05 00:00 UTC.</summary>
    public static readonly DateTimeOffset Monday = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    public sealed record Bar(decimal Open, decimal High, decimal Low, decimal Close, decimal Volume = 100m);

    public static List<Candle> Build(CandleInterval interval, DateTimeOffset start, IEnumerable<Bar> bars, string symbol = "ETHUSDT")
    {
        var span = interval.ToTimeSpan();
        return bars.Select((b, i) =>
        {
            var open = start + i * span;
            return new Candle(symbol, interval, open, open + span - TimeSpan.FromMilliseconds(1), b.Open, b.High, b.Low, b.Close, b.Volume, b.Volume * b.Close, 10, true);
        }).ToList();
    }

    /// <summary>Price reflected about <paramref name="pivot"/>: highs become lows; volumes and times stay.</summary>
    public static List<Candle> Mirror(IEnumerable<Candle> candles, decimal pivot) => candles
        .Select(c => c with { Open = 2 * pivot - c.Open, High = 2 * pivot - c.Low, Low = 2 * pivot - c.High, Close = 2 * pivot - c.Close, QuoteVolume = c.Volume * (2 * pivot - c.Close) })
        .ToList();

    /// <summary>
    /// A rising zigzag: <paramref name="cycles"/> legs of <paramref name="up"/> candles rising by <paramref name="upStep"/>
    /// then <paramref name="down"/> candles falling by <paramref name="downStep"/>. The wick in the leg's direction is
    /// <paramref name="wick"/>, the one against it half that, so every leg end is a strict fractal swing.
    /// </summary>
    public static List<Bar> Zigzag(decimal start, int up, int down, decimal upStep, decimal downStep, int cycles, decimal wick = 0.5m, decimal volume = 100m)
    {
        var bars = new List<Bar>();
        var price = start;
        for (var c = 0; c < cycles; c++)
        {
            for (var i = 0; i < up; i++)
            {
                var close = price + upStep;
                bars.Add(new Bar(price, close + wick, price - wick / 2, close, volume));
                price = close;
            }

            for (var i = 0; i < down; i++)
            {
                var close = price - downStep;
                bars.Add(new Bar(price, price + wick / 2, close - wick, close, volume));
                price = close;
            }
        }

        return bars;
    }

    /// <summary>Closes alternating inside a box, wicks inside it too.</summary>
    public static List<Bar> Box(decimal top, decimal bottom, int count, decimal volume = 100m)
    {
        var bars = new List<Bar>();
        var inset = (top - bottom) / 10;
        for (var i = 0; i < count; i++)
        {
            var open = i % 2 == 0 ? bottom + inset : top - inset;
            var close = i % 2 == 0 ? top - inset : bottom + inset;
            bars.Add(new Bar(open, top - inset / 2, bottom + inset / 2, close, volume));
        }

        return bars;
    }

    /// <summary>
    /// A rising zigzag followed by a pullback whose lows reach down to EMA(<paramref name="emaPeriod"/>) of the series so
    /// far (so a pullback to that EMA is guaranteed) and a trigger candle closing above the prior high on
    /// <paramref name="triggerVolume"/>. Returns the bars; the last bar is the trigger.
    /// </summary>
    public static List<Bar> TrendPullbackTrigger(decimal start, int cycles, int emaPeriod, int pullbackCandles, decimal pullbackStep, decimal triggerVolume,
        decimal upStep = 1m, decimal downStep = 1m, decimal wick = 1.5m, decimal volume = 100m)
    {
        // Six up, three down: every leg end is a strict fractal swing for a lookback of three, the default of every strategy.
        var bars = Zigzag(start, 6, 3, upStep, downStep, cycles, wick, volume);
        for (var i = 0; i < pullbackCandles; i++)
        {
            var open = bars[^1].Close;
            var close = open - pullbackStep;
            bars.Add(new Bar(open, open + wick / 2, close - wick / 2, close, volume));
            var ema = Ema.Last(Build(CandleInterval.FifteenMinutes, Monday, bars), emaPeriod)!.Value;
            bars[^1] = bars[^1] with { Low = Math.Min(bars[^1].Low, ema) };
        }

        var prior = bars[^1];
        var triggerClose = prior.High + 0.5m;
        bars.Add(new Bar(prior.Close, triggerClose + wick, prior.Close - wick / 2, triggerClose, triggerVolume));
        return bars;
    }

    /// <summary>Closed higher-timeframe candles grouped by their interval's open time (open first, max high, min low, last close, summed volume).</summary>
    public static List<Candle> Aggregate(IReadOnlyList<Candle> candles, CandleInterval interval) => candles
        .GroupBy(c => interval.OpenTimeOf(c.OpenTime))
        .Select(g => new Candle(g.First().Symbol, interval, g.Key, g.Key + interval.ToTimeSpan() - TimeSpan.FromMilliseconds(1),
            g.First().Open, g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close, g.Sum(c => c.Volume), g.Sum(c => c.QuoteVolume), g.Sum(c => c.TradeCount), true))
        .ToList();
}
