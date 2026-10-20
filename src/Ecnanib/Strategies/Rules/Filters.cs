using System.Globalization;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Rules;

/// <summary>The Appendix A filters, all judged at the evaluated candle's close (FR-STR-01), never at the wall clock.</summary>
internal static class Filters
{
    public const string BtcSymbol = "BTCUSDT";

    /// <summary>Entries only between the session hours (UTC); an end before the start spans midnight, equal hours mean always.</summary>
    public static FilterResult Session(DateTimeOffset at, int startHour, int endHour)
    {
        var time = TimeOnly.FromTimeSpan(at.UtcDateTime.TimeOfDay);
        var start = new TimeOnly(startHour, 0);
        var end = new TimeOnly(endHour, 0);
        var inside = startHour == endHour || (start < end ? time >= start && time < end : time >= start || time < end);
        var detail = $"{time:HH\\:mm} UTC {(inside ? "within" : "outside")} {start:HH\\:mm}-{end:HH\\:mm}";
        return inside ? FilterResult.Passed("session", detail) : FilterResult.Blocked("session", detail);
    }

    /// <summary>The user's "no new entry" windows (FR-STR-05).</summary>
    public static FilterResult NoEntryWindows(IEnumerable<EntryWindow> windows, DateTimeOffset at)
    {
        var active = windows.OrderBy(w => w.Id).FirstOrDefault(w => EntryWindowSchedule.IsActive(w, at));
        return active is null
            ? FilterResult.Passed("noEntryWindows", "none active")
            : FilterResult.Blocked("noEntryWindows", Describe(active));
    }

    /// <summary>Size multiplier on Saturday and Sunday (UTC); informational, carried on the setup.</summary>
    public static FilterResult Weekend(DateTimeOffset at, decimal multiplier)
    {
        var day = at.UtcDateTime.DayOfWeek;
        return day is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? FilterResult.Info("weekend", $"{day}: size × {Prices.Format(multiplier)}", multiplier)
            : FilterResult.Info("weekend", $"{day}: full size", 1m);
    }

    /// <summary>No new entries on the crowded side when the funding rate is at or beyond the extreme (futures only).</summary>
    public static FilterResult FundingRate(MarkPrice? mark, decimal extreme, Direction d)
    {
        if (mark is null)
            return FilterResult.Info("fundingRate", "n/a");
        var crowded = d.Sign * mark.FundingRate >= extreme;
        var detail = $"{Prices.Format(mark.FundingRate)} {(crowded ? "at or beyond" : "below")} {Prices.Format(extreme)} for {d.Word}s";
        return crowded ? FilterResult.Blocked("fundingRate", detail) : FilterResult.Passed("fundingRate", detail);
    }

    /// <summary>The funding the position would pay or earn over the expected hold, for the Risk Manager (futures only).</summary>
    public static FilterResult ProjectedFunding(MarkPrice? mark, int expectedHoldCandles, TimeSpan candle, Direction d, out decimal? projected)
    {
        projected = null;
        if (mark is null)
            return FilterResult.Info("projectedFunding", "n/a");
        var periods = (decimal)(expectedHoldCandles * candle.TotalHours / 8);
        projected = mark.FundingRate * periods;
        var cost = d.Sign * projected.Value;
        return FilterResult.Info("projectedFunding", $"{Prices.Format(cost * 100)}% over {expectedHoldCandles} candles ({(cost > 0 ? $"{d.Word}s pay" : cost < 0 ? $"{d.Word}s earn" : "flat")})");
    }

    /// <summary>No entry when price is more than the maximum percent beyond the slow EMA.</summary>
    public static FilterResult Extension(decimal close, decimal ema, decimal maxPercent, Direction d, out decimal percent)
    {
        percent = ema == 0 ? 0 : (close - ema) / ema * 100;
        var extended = d.Sign * percent > maxPercent;
        var detail = $"{Prices.Format(d.Sign * percent)}% {(extended ? "beyond" : "within")} {Prices.Format(maxPercent)}% {d.Above} EMA";
        return extended ? FilterResult.Blocked("extension", detail) : FilterResult.Passed("extension", detail);
    }

    /// <summary>Minimum average daily quote volume for symbols other than BTC and ETH.</summary>
    public static FilterResult Liquidity(string symbol, IReadOnlyList<Candle> candles, int period, decimal minimum, out decimal? average)
    {
        average = VolumeAverage.QuoteBefore(candles, candles.Count, period);
        if (symbol.StartsWith("BTC", StringComparison.Ordinal) || symbol.StartsWith("ETH", StringComparison.Ordinal))
            return FilterResult.Info("liquidity", "n/a (BTC/ETH)");
        if (average is not { } volume)
            return FilterResult.Blocked("liquidity", $"no {period}-candle quote volume average");
        var detail = $"average daily quote volume {Prices.Format(volume)} {(volume < minimum ? "below" : "at least")} {Prices.Format(minimum)}";
        return volume < minimum ? FilterResult.Blocked("liquidity", detail) : FilterResult.Passed("liquidity", detail);
    }

    /// <summary>
    /// The BTC weekly regime for symbols other than BTC: blocked against a bearish regime for longs and a bullish one
    /// for shorts, and blocked when the series is unavailable (fail-safe, NFR-REL-03).
    /// </summary>
    public static FilterResult BtcRegime(string symbol, IReadOnlyList<Candle> btcWeekly, int emaPeriod, int swingLookback, Direction d, out int? regime, out decimal? ema)
    {
        regime = null;
        ema = null;
        if (symbol.StartsWith("BTC", StringComparison.Ordinal))
            return FilterResult.Info("btcRegime", "n/a (BTC)");
        ema = Ema.Last(btcWeekly, emaPeriod);
        if (btcWeekly.Count < emaPeriod || ema is not { } value)
            return FilterResult.Blocked("btcRegime", "BTC weekly series unavailable");
        var structure = Swings.Classify(Swings.Find(btcWeekly, swingLookback));
        var close = btcWeekly[^1].Close;
        regime = close > value && structure != SwingStructure.LowerHighsLowerLows ? 1
            : close < value && structure != SwingStructure.HigherHighsHigherLows ? -1
            : 0;
        var word = regime switch { 1 => "bullish", -1 => "bearish", _ => "neutral" };
        var detail = $"BTC weekly {word} (close {Prices.Format(close)} vs EMA{emaPeriod} {Prices.Format(value)})";
        return regime == -d.Sign ? FilterResult.Blocked("btcRegime", detail) : FilterResult.Passed("btcRegime", detail);
    }

    private static string Describe(EntryWindow window) => window.Kind == EntryWindowKind.Recurring
        ? $"recurring {window.StartDay} {window.StartTime:HH\\:mm} → {window.EndDay} {window.EndTime:HH\\:mm}"
        : $"one-off {window.StartAt?.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture)} → {window.EndAt?.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture)}";
}
