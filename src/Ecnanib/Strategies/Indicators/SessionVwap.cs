using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Indicators;

/// <summary>Volume-weighted average of the typical price (high + low + close) / 3 since 00:00 UTC of the candle's day; null while the session has no volume.</summary>
internal static class SessionVwap
{
    public static decimal?[] Compute(IReadOnlyList<Candle> candles)
    {
        var result = new decimal?[candles.Count];
        DateOnly? session = null;
        decimal priceVolume = 0;
        decimal volume = 0;
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var day = DateOnly.FromDateTime(candle.OpenTime.UtcDateTime);
            if (session != day)
            {
                session = day;
                priceVolume = 0;
                volume = 0;
            }

            priceVolume += (candle.High + candle.Low + candle.Close) / 3 * candle.Volume;
            volume += candle.Volume;
            result[i] = volume == 0 ? null : priceVolume / volume;
        }

        return result;
    }

    public static decimal? Last(IReadOnlyList<Candle> candles) => candles.Count == 0 ? null : Compute(candles)[^1];
}
