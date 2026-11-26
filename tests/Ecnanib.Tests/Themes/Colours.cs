using System.Globalization;

namespace Ecnanib.Tests.Themes;

/// <summary>
/// What the tests of the stylesheets measure colours by: the page of each theme, and the
/// contrast of two colours as WCAG 2.1 defines it. <see cref="ThemeStyleTests"/> holds the text
/// of the themes to it, and <c>AccessibilityStyleTests</c> what shows where the keyboard is and
/// the line around a control. A colour is written as <c>#rrggbb</c>.
/// </summary>
internal static class Colours
{
    /// <summary>The page of a light theme, which a browser paints white.</summary>
    public const string LightPage = "#ffffff";

    /// <summary>
    /// The page of a dark theme. A browser paints the page itself, in a grey of its own: Chrome
    /// and Edge #121212, Firefox #1c1b22, and Safari #1e1e1e. This is the lightest of them, so
    /// a colour that reads on it reads on the darker ones too.
    /// </summary>
    public const string DarkPage = "#1e1e1e";

    /// <summary>The contrast of two colours as WCAG 2.1 defines it, from 1 to 21.</summary>
    public static double Contrast(string one, string other)
    {
        var (lighter, darker) = (Math.Max(Luminance(one), Luminance(other)), Math.Min(Luminance(one), Luminance(other)));

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>The relative luminance of a colour that is written as <c>#rrggbb</c>.</summary>
    public static double Luminance(string colour)
    {
        double Part(int at)
        {
            var value = PartOf(colour, at) / 255.0;

            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Part(1)) + (0.7152 * Part(3)) + (0.0722 * Part(5));
    }

    /// <summary>
    /// What a colour looks like on a page when only a share of it is there, and the page shows
    /// through the rest: the colour that <c>color-mix(in srgb, currentColor 40%, transparent)</c>
    /// is on the page, for a share of 0.4.
    /// </summary>
    /// <param name="colour">The colour that is mixed, which is the colour of the text.</param>
    /// <param name="share">How much of it is in the mix, from 0 to 1.</param>
    /// <param name="page">The page that it is drawn on.</param>
    public static string Over(string colour, double share, string page)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(share, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(share, 1);

        // Mixed with a colour that is not there, a colour keeps its parts, and is there as much
        // as its share. On a page, each part is then that much of the colour, and the rest of the page.
        int Part(int at) => (int)Math.Round((PartOf(colour, at) * share) + (PartOf(page, at) * (1 - share)), MidpointRounding.AwayFromZero);

        return string.Create(CultureInfo.InvariantCulture, $"#{Part(1):x2}{Part(3):x2}{Part(5):x2}");
    }

    /// <summary>One of the three parts of a colour, from 0 to 255: the one that is written at a place of it.</summary>
    private static int PartOf(string colour, int at) =>
        int.Parse(colour.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
