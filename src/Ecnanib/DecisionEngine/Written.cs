using System.Globalization;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// How a prompt writes a number and a time: the same in every country and on every system, so
/// that the same parts give the same prompt. See docs/prompts.md.
/// </summary>
internal static class Written
{
    // Every digit that a decimal has, and no zero after the last of them: 1.50 is written 1.5.
    private const string EveryDigit = "0.############################";

    /// <summary>A number with every digit that it has, and no zero after the last of them.</summary>
    public static string Number(decimal value) => value.ToString(EveryDigit, CultureInfo.InvariantCulture);

    /// <summary>
    /// A number that was worked out, such as an average, to so many significant digits: the
    /// digits before the point are all written, and what follows it is rounded.
    /// </summary>
    public static string Number(decimal value, int significantDigits)
    {
        if (value == 0m)
        {
            return "0";
        }

        // How many digits stand before the point. Below 1 it counts the zeros after it, as less than nothing.
        var before = (int)Math.Floor(Math.Log10((double)Math.Abs(value))) + 1;
        var after = Math.Clamp(significantDigits - before, 0, 28);

        return Number(Math.Round(value, after, MidpointRounding.AwayFromZero));
    }

    /// <summary>A time in UTC, to the minute: <c>2026-11-16T10:15Z</c>.</summary>
    public static string Time(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);
}
