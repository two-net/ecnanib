using Ecnanib.Database.Entities;

namespace Ecnanib.Execution.Abstractions;

/// <summary>The arithmetic the pages share for P&amp;L figures (FR-UI-01 dashboard, FR-UI-03 d), so no view computes it differently.</summary>
public static class PnlMath
{
    /// <summary>The open P&amp;L of a position at <paramref name="price"/>, in the quote asset, before fees.</summary>
    public static decimal Unrealized(PositionSide side, decimal size, decimal entryPrice, decimal price) =>
        side == PositionSide.Long ? (price - entryPrice) * size : (entryPrice - price) * size;

    /// <summary>Midnight UTC of the day <paramref name="now"/> falls in: the day the daily loss limit counts (FR-EXE-09).</summary>
    public static DateTimeOffset UtcDayStart(DateTimeOffset now) => new(now.UtcDateTime.Date, TimeSpan.Zero);

    /// <summary>The CSS class that colours a signed figure; empty for zero or unknown, so colour never carries meaning alone.</summary>
    public static string SignClass(decimal? value) => value switch
    {
        > 0 => "pnl-up",
        < 0 => "pnl-down",
        _ => "",
    };
}
