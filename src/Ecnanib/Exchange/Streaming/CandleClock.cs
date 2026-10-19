using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Streaming;

/// <summary>
/// UTC-aligned candle boundaries (FR-AGT-09), forwarding to <see cref="CandleIntervalExtensions"/> so that other
/// modules reach the same arithmetic through the abstractions alone.
/// </summary>
internal static class CandleClock
{
    public static DateTimeOffset OpenTimeOf(DateTimeOffset time, CandleInterval interval) => interval.OpenTimeOf(time);

    public static DateTimeOffset NextCloseAfter(DateTimeOffset time, CandleInterval interval) => interval.NextCloseAfter(time);
}
