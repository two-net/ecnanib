using System.Globalization;

namespace Ecnanib.Strategies.Indicators;

/// <summary>Output rounding (8 decimals, away from zero) with the scale normalised, so equal values serialize identically (FR-STR-01).</summary>
internal static class Prices
{
    public static decimal Round(decimal value) => Normalize(decimal.Round(value, 8, MidpointRounding.AwayFromZero));

    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    public static string Format(decimal value) => Round(value).ToString(CultureInfo.InvariantCulture);
}
