using System.Globalization;
using System.Text;
using Ecnanib.Kernel;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// How values are written into a prompt: in the invariant culture, with UTC times, so a prompt doesn't depend on
/// the server's settings, and with untrusted text escaped so it can't close or forge a tag (FR-PRM-06).
/// </summary>
internal static class PromptText
{
    /// <summary>Significant digits kept for a computed value, such as an EMA, which would otherwise run to 28 places.</summary>
    public const int SignificantDigits = 8;

    /// <summary>The longest attribute, such as a title or a source.</summary>
    public const int AttributeMaxLength = 300;

    public const string Truncated = "[truncated]";

    /// <summary>A value as Binance or the agent gave it, without trailing zeros. A decimal never prints an exponent.</summary>
    public static string Number(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);

        return text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    public static string Number(decimal? value) => value is { } number ? Number(number) : "none";

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A computed value, to <see cref="SignificantDigits"/> significant digits, in decimal throughout.</summary>
    public static string Computed(decimal? value) => value is { } number ? Number(Significant(number)) : "n/a";

    public static decimal Significant(decimal value)
    {
        if (value == 0m)
        {
            return 0m;
        }

        var magnitude = Math.Abs(value);
        var exponent = 0;
        while (magnitude >= 10m)
        {
            magnitude /= 10m;
            exponent++;
        }

        while (magnitude < 1m)
        {
            magnitude *= 10m;
            exponent--;
        }

        return Math.Round(value, Math.Clamp(SignificantDigits - 1 - exponent, 0, 28), MidpointRounding.AwayFromZero);
    }

    /// <summary>A fraction as a percentage, such as 0.0001 as 0.01%.</summary>
    public static string Percent(decimal fraction) => Number(fraction * 100m) + "%";

    public static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string TimeframeName(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => "15m",
        Timeframe.OneHour => "1H",
        Timeframe.FourHours => "4H",
        Timeframe.OneDay => "1D",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Not a timeframe."),
    };

    /// <summary>A candle's length as a timeframe's name, such as 1H, or 1W for a week.</summary>
    public static string IntervalName(TimeSpan interval) => interval switch
    {
        { TotalDays: 7 } => "1W",
        { TotalDays: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{interval.TotalDays}D"),
        { TotalHours: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{interval.TotalHours}H"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{interval.TotalMinutes}m"),
    };

    /// <summary>An enum's name in snake case, such as role_output, for a tag's attribute.</summary>
    public static string Snake<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Text that a person or a model wrote, made safe to put between tags: line breaks become \n, other control
    /// characters go, and &amp;, &lt; and &gt; are escaped, so the text can't close its block or open a tag.
    /// </summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    builder.Append('\n');
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n' or '\t':
                    builder.Append(c);
                    break;
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Untrusted text as an attribute's value: escaped, quotes too, on one line, and cut short if long.</summary>
    public static string Attribute(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var cut = Cut(line, AttributeMaxLength);

        return Escape(cut.Length < line.Length ? cut + "…" : cut).Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    /// <summary>Untrusted text cut to <paramref name="maxLength"/> characters and marked if it was, then escaped.</summary>
    public static string Body(string text, int maxLength)
    {
        var trimmed = text.Trim();
        var cut = Cut(trimmed, maxLength);

        return Escape(cut.Length < trimmed.Length ? cut.TrimEnd() + "\n" + Truncated : cut);
    }

    /// <summary>The first <paramref name="maxLength"/> characters, without splitting a surrogate pair.</summary>
    private static string Cut(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;

        return text[..length];
    }
}
