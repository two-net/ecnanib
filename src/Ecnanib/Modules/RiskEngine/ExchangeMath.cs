namespace Ecnanib.Modules.RiskEngine;

/// <summary>
/// Rounding to Binance's grids (FR-RISK-04). A filter's valid values are its minimum plus a whole number of
/// steps, so the grid starts at the minimum, not at zero. A step of 0 means Binance turned the check off.
/// Decimal throughout (DATA-R-02), so a value already on the grid never moves.
/// </summary>
internal static class ExchangeMath
{
    /// <summary>The largest grid value at or below <paramref name="value"/>.</summary>
    public static decimal Down(decimal value, decimal step, decimal origin) =>
        step <= 0m ? value : origin + (decimal.Floor((value - origin) / step) * step);

    /// <summary>The smallest grid value at or above <paramref name="value"/>.</summary>
    public static decimal Up(decimal value, decimal step, decimal origin) =>
        step <= 0m ? value : origin + (decimal.Ceiling((value - origin) / step) * step);

    /// <summary>Whether a price is outside a filter's range. A 0 bound is no bound.</summary>
    public static bool OutOfRange(decimal value, decimal minimum, decimal maximum) =>
        (minimum > 0m && value < minimum) || (maximum > 0m && value > maximum);

    /// <summary>The same value with no trailing zeros, so a trace reads 0.5 rather than 0.50000000.</summary>
    public static decimal Normalize(decimal value) => value / 1.000000000000000000000000000000000m;
}
