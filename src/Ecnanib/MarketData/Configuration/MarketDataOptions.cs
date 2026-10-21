using System.ComponentModel.DataAnnotations;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.MarketData.Configuration;

/// <summary>
/// The <c>MarketData</c> section (Appendix D "market data"; FR-DATA-01, FR-DATA-02): how many closed candles each
/// series holds, keyed by interval, which is both the depth backfilled when an agent starts and the window every
/// evaluation sees (a fixed window keeps the engine's seeded indicators deterministic, FR-STR-01); and how often a
/// loaded symbol listing is refreshed, at most a day apart. Candle retention is <c>Retention:CandleDays</c>, the
/// database module's (NFR-DATA-03).
/// </summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    public const int MinBackfill = 20;

    public const int MaxBackfill = 5000;

    /// <summary>Closed candles per series, keyed by interval label: 15m, 1H, 4H, 1D, 1W.</summary>
    public Dictionary<string, int> BackfillCandles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["15m"] = 500,
        ["1H"] = 500,
        ["4H"] = 500,
        ["1D"] = 400,
        ["1W"] = 200,
    };

    /// <summary>Minutes between refreshes of each loaded symbol listing; a day at most (FR-DATA-01).</summary>
    [Range(1, 1440)]
    public int SymbolRefreshIntervalMinutes { get; set; } = 360;

    /// <summary>Every interval a series can have: the four agent timeframes and the weekly higher timeframe (FR-STR-03).</summary>
    public static readonly IReadOnlyList<CandleInterval> Intervals =
    [
        CandleInterval.FifteenMinutes, CandleInterval.OneHour, CandleInterval.FourHours, CandleInterval.OneDay, CandleInterval.OneWeek,
    ];

    /// <summary>The key of an interval in <see cref="BackfillCandles"/> (the SRS spelling: 15m, 1H, 4H, 1D, plus 1W).</summary>
    public static string KeyOf(CandleInterval interval) => interval switch
    {
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.OneHour => "1H",
        CandleInterval.FourHours => "4H",
        CandleInterval.OneDay => "1D",
        CandleInterval.OneWeek => "1W",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Not a candle interval."),
    };

    public int BackfillFor(CandleInterval interval) => BackfillCandles.TryGetValue(KeyOf(interval), out var count) ? count : 0;

    public TimeSpan SymbolRefreshInterval => TimeSpan.FromMinutes(SymbolRefreshIntervalMinutes);
}
