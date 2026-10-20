using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Indicators;

/// <summary>The envelope of the candles <c>[Start, End]</c>: no close lies outside it by construction.</summary>
internal sealed record RangeBox(int Start, int End, decimal Top, decimal Bottom)
{
    public decimal Height => Top - Bottom;
}

internal static class Ranges
{
    /// <summary>
    /// The box over the <paramref name="count"/> candles before <paramref name="breakoutIndex"/>, or null when there
    /// are not enough candles, the box is flat, or it is taller than <paramref name="maxHeightAtr"/> × <paramref name="atr"/>.
    /// </summary>
    public static RangeBox? Box(IReadOnlyList<Candle> candles, int breakoutIndex, int count, decimal maxHeightAtr, decimal atr)
    {
        var start = breakoutIndex - count;
        var end = breakoutIndex - 1;
        if (count < 1 || start < 0 || breakoutIndex >= candles.Count)
            return null;

        var top = candles[start].High;
        var bottom = candles[start].Low;
        for (var i = start + 1; i <= end; i++)
        {
            top = Math.Max(top, candles[i].High);
            bottom = Math.Min(bottom, candles[i].Low);
        }

        var height = top - bottom;
        if (height <= 0 || height > maxHeightAtr * atr)
            return null;
        return new RangeBox(start, end, top, bottom);
    }
}
