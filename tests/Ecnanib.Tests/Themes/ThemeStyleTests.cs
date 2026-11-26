using System.Text.RegularExpressions;

namespace Ecnanib.Tests.Themes;

/// <summary>
/// System follows the device's color-scheme preference and switches live when that preference
/// changes (FR-THM-2), and every screen works in both themes, with text contrast meeting WCAG
/// 2.1 AA (FR-THM-5). These tests hold the stylesheets to what that rests on: the three rules
/// that make a page dark, light, or as the device is set, that no rule asks the device over the
/// choice of a user, and that a colour which is written out reads in both themes. See
/// docs/themes.md.
/// </summary>
/// <remarks>
/// None of these tests drives a browser: that a page looks right in a theme is seen by hand,
/// as docs/themes.md says, and the contrast of what every screen shows is measured in one by
/// <c>AccessibilityBrowserTests</c>, when a run is told to. What is held here are the rules of
/// the stylesheets, as the application serves them.
/// </remarks>
public partial class ThemeStyleTests
{
    // What WCAG 2.1 AA asks of text of the size of the text of a page (1.4.3).
    private const double ForText = 4.5;

    [Fact]
    public async Task A_page_is_dark_light_or_as_the_device_is_set_by_what_its_first_element_says()
    {
        var styles = await StylesheetAsync();

        // A page that says nothing may be either, and the browser takes the one of the device,
        // also when the setting of the device changes while the page is open.
        Assert.Matches(@":root\s*\{\s*color-scheme:\s*light dark;", styles);

        // A page that says one is that one, whatever the device is set to.
        Assert.Matches(@":root\[data-theme=""light""\]\s*\{\s*color-scheme:\s*light;\s*\}", styles);
        Assert.Matches(@":root\[data-theme=""dark""\]\s*\{\s*color-scheme:\s*dark;\s*\}", styles);
    }

    [Fact]
    public async Task No_rule_asks_the_device_what_it_is_set_to()
    {
        // A rule that did would hold for a user who chose the other theme: their page would be
        // light with the colours of a dark one, or the other way round.
        foreach (var (name, styles) in await StylesheetsAsync())
        {
            Assert.False(
                styles.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase),
                $"{name} asks the device for its colours, over the theme that a user chose. Give the colour a light and a dark value with light-dark().");
        }
    }

    [Fact]
    public async Task A_colour_of_the_themes_reads_on_the_page_of_each()
    {
        var colours = Themed().Matches(await StylesheetAsync());

        // The colour of what is wrong, and the colour of a warning.
        Assert.Equal(["--danger", "--warning"], colours.Select(colour => colour.Groups["name"].Value));

        Assert.All(colours, colour =>
        {
            var name = colour.Groups["name"].Value;
            var light = colour.Groups["light"].Value;
            var dark = colour.Groups["dark"].Value;

            Assert.True(
                Colours.Contrast(light, Colours.LightPage) >= ForText,
                $"{name} is {light} on a light page, with a contrast of {Colours.Contrast(light, Colours.LightPage):0.##} to 1.");
            Assert.True(
                Colours.Contrast(dark, Colours.DarkPage) >= ForText,
                $"{name} is {dark} on a dark page, with a contrast of {Colours.Contrast(dark, Colours.DarkPage):0.##} to 1.");
        });
    }

    [Fact]
    public async Task A_colour_that_is_written_out_is_a_colour_of_the_themes_or_brings_its_own_background()
    {
        var styles = Stylesheets.WithoutComments(await StylesheetAsync());

        // Every colour that the stylesheet of the pages writes out, by the rule that it is in.
        // Anything else is the colour of the text around it, or a colour of the system, which
        // the browser gives the value of the theme.
        var written = WrittenOut().Matches(styles)
            .Select(colour => RuleOf(styles, colour.Index))
            .Distinct()
            .Order(StringComparer.Ordinal);

        // The colours of the themes, each with a light and a dark value, and the badge of Live,
        // which is filled, and so is the same on both.
        Assert.Equal([".mode-live", ":root"], written);

        // White on the red of the badge.
        var badge = Regex.Match(styles, @"\.mode-live\s*\{([^}]*)\}").Groups[1].Value;
        var fill = Regex.Match(badge, "background-color:\\s*(#[0-9a-fA-F]{6})").Groups[1].Value;
        var text = Regex.Match(badge, "(?<![-\\w])color:\\s*(#[0-9a-fA-F]{6})").Groups[1].Value;

        Assert.True(Colours.Contrast(text, fill) >= ForText, $"The badge of Live is {text} on {fill}, with a contrast of {Colours.Contrast(text, fill):0.##} to 1.");
    }

    [Fact]
    public async Task The_button_of_the_dialog_that_rejoins_the_server_reads()
    {
        // The dialog is light in both themes, and brings its own colours. Its button is white
        // on a blue, also under a pointer and while it is pressed. Its look is in the stylesheet
        // of the styles beside a component: the one of the pages names the dialog too, for what
        // moves of it, and says nothing of its button.
        var styles = (await StylesheetsAsync()).Single(sheet => sheet.Styles.Contains("#components-reconnect-modal button", StringComparison.Ordinal)).Styles;

        Assert.Matches(@"#components-reconnect-modal(\[[^\]]*\])?\s*\{[^}]*color-scheme:\s*light only;", styles);

        var buttons = Regex.Matches(styles, @"#components-reconnect-modal button[^{,]*\{([^}]*)\}");
        var fills = buttons
            .Select(button => Regex.Match(button.Groups[1].Value, "background-color:\\s*(#[0-9a-fA-F]{6})"))
            .Where(fill => fill.Success)
            .Select(fill => fill.Groups[1].Value)
            .ToList();

        Assert.Equal(3, fills.Count);
        Assert.All(fills, fill => Assert.True(
            Colours.Contrast("#ffffff", fill) >= ForText,
            $"The button is white on {fill}, with a contrast of {Colours.Contrast("#ffffff", fill):0.##} to 1."));
    }

    [Theory]
    [InlineData("#000000", "#ffffff", 21)]
    [InlineData("#ffffff", "#ffffff", 1)]
    [InlineData("#767676", "#ffffff", 4.54)]
    [InlineData("#b3261e", "#ffffff", 6.54)]
    public void The_contrast_of_two_colours_is_the_one_of_WCAG(string colour, string page, double contrast)
    {
        Assert.Equal(contrast, Colours.Contrast(colour, page), 0.01);
        Assert.Equal(contrast, Colours.Contrast(page, colour), 0.01);
    }

    /// <summary>The stylesheet of the pages, as the application serves it.</summary>
    private static Task<string> StylesheetAsync() => Stylesheets.OfThePagesAsync();

    /// <summary>Every stylesheet that a page names, as the application serves it.</summary>
    private static Task<IReadOnlyList<(string Name, string Styles)>> StylesheetsAsync() => Stylesheets.AllAsync();

    /// <summary>The selector of the rule that a place in a stylesheet is in.</summary>
    private static string RuleOf(string styles, int index) => Stylesheets.RuleOf(styles, index);

    // A colour of the themes: a name, a light value, and a dark one.
    [GeneratedRegex(@"(?<name>--[a-z-]+):\s*light-dark\((?<light>#[0-9a-fA-F]{6}),\s*(?<dark>#[0-9a-fA-F]{6})\);")]
    private static partial Regex Themed();

    // A colour by its number, or as a function of its parts. A colour that is mixed from the
    // colour of the text is none of these.
    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\(")]
    private static partial Regex WrittenOut();
}
