using System.Globalization;
using Ecnanib.Kernel;

namespace Ecnanib.Platform.Contracts;

/// <summary>
/// Reads whole-number settings, and settings that take one value per timeframe, keyed as SRS 7.3 writes them: 15m, 1H,
/// 4H and 1D. A missing value takes its default, and an invalid one throws with a message that names it (CFG-03). The
/// scheduler's cycle timings and the decision cycle's approval windows share it, because both are in the Agents section.
/// </summary>
public static class SettingValues
{
    public static IReadOnlyList<Timeframe> Timeframes { get; } =
        [Timeframe.FifteenMinutes, Timeframe.OneHour, Timeframe.FourHours, Timeframe.OneDay];

    /// <summary>The name the configuration uses for <paramref name="timeframe"/>, as in SRS 7.3.</summary>
    public static string Label(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => "15m",
        Timeframe.OneHour => "1H",
        Timeframe.FourHours => "4H",
        Timeframe.OneDay => "1D",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null),
    };

    /// <exception cref="InvalidOperationException">The value isn't a whole number from <paramref name="minimum"/> to <paramref name="maximum"/>.</exception>
    public static int WholeNumber(IConfiguration configuration, string key, int fallback, int minimum, int maximum)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{key} must be a whole number.");
        }

        if (number < minimum || number > maximum)
        {
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        }

        return number;
    }

    /// <summary>
    /// A whole number of minutes per timeframe, under <paramref name="key"/>, such as <c>Agents:CycleDeadlineMinutes:15m</c>.
    /// Each must be less than its timeframe's <paramref name="interval"/>, so it ends before the next candle closes.
    /// </summary>
    /// <exception cref="InvalidOperationException">A key isn't a timeframe, or a value is invalid.</exception>
    public static IReadOnlyDictionary<Timeframe, TimeSpan> MinutesPerTimeframe(
        IConfiguration configuration,
        string key,
        IReadOnlyDictionary<Timeframe, int> defaults,
        int minimum,
        Func<Timeframe, TimeSpan> interval)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(interval);

        var labels = Timeframes.Select(Label).ToList();

        foreach (var child in configuration.GetSection(key).GetChildren())
        {
            if (!labels.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{key}:{child.Key} isn't a timeframe. Use {string.Join(", ", labels)}.");
            }
        }

        return Timeframes.ToDictionary(
            timeframe => timeframe,
            timeframe => TimeSpan.FromMinutes(WholeNumber(
                configuration,
                $"{key}:{Label(timeframe)}",
                defaults[timeframe],
                minimum,
                (int)interval(timeframe).TotalMinutes - 1)));
    }
}
