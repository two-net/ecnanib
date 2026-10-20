using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Rules;

/// <summary>The trigger-candle tests of Appendix A.</summary>
internal static class Triggers
{
    /// <summary>A directional candle closing beyond the prior close ("close in the trend direction").</summary>
    public static bool ClosesInDirection(Candle candle, Candle prior, Direction d) =>
        d.Sign * (candle.Close - candle.Open) > 0 && d.Sign * (candle.Close - prior.Close) > 0;

    /// <summary>A close beyond the prior candle's high (long) or low (short).</summary>
    public static bool ClosesBeyondPrior(Candle candle, Candle prior, Direction d) =>
        d.IsLong ? candle.Close > prior.High : candle.Close < prior.Low;

    /// <summary>A directional candle closing on the right side of a level.</summary>
    public static bool HoldsLevel(Candle candle, decimal level, Direction d) =>
        d.Sign * (candle.Close - level) > 0 && d.Sign * (candle.Close - candle.Open) > 0;

    /// <summary>The candle's volume over the average of the <paramref name="period"/> candles before it; null without an average.</summary>
    public static decimal? VolumeRatio(IReadOnlyList<Candle> candles, int index, int period)
    {
        var average = VolumeAverage.Before(candles, index, period);
        return average is > 0 ? candles[index].Volume / average : null;
    }
}
