using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Indicators;

/// <summary>Simple averages of volume over the <c>period</c> candles before an index (the candle at the index excluded).</summary>
internal static class VolumeAverage
{
    public static decimal? Before(IReadOnlyList<Candle> candles, int index, int period) => Average(candles, index, period, c => c.Volume);

    public static decimal? QuoteBefore(IReadOnlyList<Candle> candles, int index, int period) => Average(candles, index, period, c => c.QuoteVolume);

    private static decimal? Average(IReadOnlyList<Candle> candles, int index, int period, Func<Candle, decimal> select)
    {
        var from = index - period;
        if (period < 1 || from < 0 || index > candles.Count)
            return null;
        decimal sum = 0;
        for (var i = from; i < index; i++)
            sum += select(candles[i]);
        return sum / period;
    }
}
