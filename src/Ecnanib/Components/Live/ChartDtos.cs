using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Components.Live;

/// <summary>One candlestick as the chart library takes it: the open time in unix seconds, prices and volume as doubles.</summary>
internal sealed record ChartBar(long Time, double Open, double High, double Low, double Close, double Volume)
{
    public static ChartBar From(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        return new ChartBar(candle.OpenTime.ToUnixTimeSeconds(), (double)candle.Open, (double)candle.High, (double)candle.Low, (double)candle.Close, (double)candle.Volume);
    }
}

/// <summary>
/// A marker on the price series (FR-UI-03 c): <see cref="Position"/> is aboveBar, belowBar or inBar; <see cref="Shape"/>
/// arrowUp, arrowDown, circle or square; <see cref="Tone"/> long, short, exit or stop, which the browser maps to the
/// chart colour tokens of the current theme; <see cref="Text"/> is the label every marker also carries in words.
/// </summary>
internal sealed record ChartMarker(long Time, string Position, string Shape, string Tone, string Text, string Id);
