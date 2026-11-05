using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.MarketData;

/// <summary>Candle lengths and alignment, all in UTC, as Binance aligns them.</summary>
internal static class Timeframes
{
    /// <summary>Binance's weeks start on Monday. The Unix epoch was a Thursday.</summary>
    private static readonly DateTimeOffset FirstMonday = new(1970, 1, 5, 0, 0, 0, TimeSpan.Zero);

    public static CandleInterval Interval(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => CandleInterval.FifteenMinutes,
        Timeframe.OneHour => CandleInterval.OneHour,
        Timeframe.FourHours => CandleInterval.FourHours,
        Timeframe.OneDay => CandleInterval.OneDay,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe)),
    };

    /// <summary>The higher-timeframe context SRS A.1 gives each timeframe.</summary>
    public static CandleInterval Higher(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => CandleInterval.OneHour,
        Timeframe.OneHour or Timeframe.FourHours => CandleInterval.OneDay,
        Timeframe.OneDay => CandleInterval.OneWeek,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe)),
    };

    public static TimeSpan Length(CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
        CandleInterval.OneHour => TimeSpan.FromHours(1),
        CandleInterval.FourHours => TimeSpan.FromHours(4),
        CandleInterval.OneDay => TimeSpan.FromDays(1),
        CandleInterval.OneWeek => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };

    /// <summary>The open time of the candle that contains <paramref name="at"/>.</summary>
    public static DateTimeOffset Floor(CandleInterval interval, DateTimeOffset at)
    {
        var anchor = interval == CandleInterval.OneWeek ? FirstMonday : DateTimeOffset.UnixEpoch;
        var length = Length(interval).Ticks;
        var since = at.UtcTicks - anchor.UtcTicks;

        return new DateTimeOffset(at.UtcTicks - (((since % length) + length) % length), TimeSpan.Zero);
    }

    /// <summary>The open time of the last candle that had closed by <paramref name="at"/>.</summary>
    public static DateTimeOffset LastClosed(CandleInterval interval, DateTimeOffset at) => Floor(interval, at) - Length(interval);
}
