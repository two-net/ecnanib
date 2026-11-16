using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The higher timeframes that are the context of each timeframe of an agent (SRS 4.6): the data
/// package of a cycle holds their candles beside those of the agent's own timeframe (FR-CYC-2),
/// and the timeframe module of the prompt names the same. See "The data package" in
/// docs/cycles.md.
/// </summary>
public static class TimeframeContext
{
    private static readonly CandleInterval[] OfFifteenMinutes = [CandleInterval.OneHour, CandleInterval.FourHours];
    private static readonly CandleInterval[] OfOneHour = [CandleInterval.FourHours, CandleInterval.OneDay];
    private static readonly CandleInterval[] OfFourHours = [CandleInterval.OneDay, CandleInterval.OneWeek];
    private static readonly CandleInterval[] OfOneDay = [CandleInterval.OneWeek];

    /// <summary>
    /// The lengths of the candles that are the context of a timeframe, shortest first: 1H and 4H
    /// for 15m, 4H and 1D for 1H, 1D and 1W for 4H, and 1W for 1D.
    /// </summary>
    public static IReadOnlyList<CandleInterval> Of(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => OfFifteenMinutes,
        Timeframe.OneHour => OfOneHour,
        Timeframe.FourHours => OfFourHours,
        Timeframe.OneDay => OfOneDay,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "The timeframe has no context."),
    };

    /// <summary>
    /// The code of a length of a candle, as a prompt writes it: the code of the timeframe of the
    /// same name, and <c>1W</c> for the week, which is context alone.
    /// </summary>
    public static string Code(CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => Timeframe.FifteenMinutes.Code(),
        CandleInterval.OneHour => Timeframe.OneHour.Code(),
        CandleInterval.FourHours => Timeframe.FourHours.Code(),
        CandleInterval.OneDay => Timeframe.OneDay.Code(),
        CandleInterval.OneWeek => "1W",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "The length of a candle has no code."),
    };
}
