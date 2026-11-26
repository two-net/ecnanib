using System.Globalization;
using System.Text.RegularExpressions;
using Ecnanib.Tests.Themes;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// The UI is fully operable by keyboard (NFR-USA-1), which takes that whoever uses a keyboard
/// sees where they are: what has the focus is outlined, in a colour that reads on the page of
/// either theme (WCAG 2.4.7, 1.4.11), and so does the line that shows a control to be one.
/// These tests hold the stylesheets to that, and to the exceptions of
/// <see cref="AccessibilityRules"/>. See docs/accessibility.md.
/// </summary>
/// <remarks>
/// What is held are the rules of the stylesheets as they are written, and as the application
/// serves them: that a control which has the focus is drawn with its outline is seen by the run
/// that drives a browser.
/// </remarks>
public partial class AccessibilityStyleTests
{
    // What WCAG 2.1 AA asks of what shows a control, and of what shows where the keyboard is (1.4.11).
    private const double ForControls = 3;

    // The colour of the text of a page, which the browser gives the value of the theme: black
    // on a light page, and white on a dark one. A rule names it as CanvasText, and a colour
    // that is mixed from the colour of the text is mixed from it.
    private const string TextColour = "CanvasText";
    private const string TextOnLight = "#000000";
    private const string TextOnDark = "#ffffff";

    [Fact]
    public async Task What_has_the_focus_of_the_keyboard_is_outlined_in_both_themes()
    {
        var focused = (await RulesAsync()).Where(rule => rule.Selector == ":focus-visible").ToList();

        Assert.True(
            focused.Count > 0,
            "No rule of the stylesheets is for :focus-visible alone, so what has the focus of the keyboard is drawn as each " +
            $"browser draws it, which need not read on a page. Give :focus-visible an outline of 2px solid {TextColour}.");

        var outline = focused.Select(rule => Outline().Match(rule.Declarations)).LastOrDefault(outline => outline.Success);

        Assert.True(outline is not null, $"The rule for :focus-visible sets no outline. Give it an outline of 2px solid {TextColour}.");

        var parts = outline.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var width = parts.Select(part => Pixels().Match(part)).FirstOrDefault(pixels => pixels.Success);

        Assert.True(
            width is not null
                && double.Parse(width.Groups[1].Value, CultureInfo.InvariantCulture) >= 2
                && parts.Contains("solid", StringComparer.OrdinalIgnoreCase),
            $"The outline of :focus-visible is \"{outline.Groups[1].Value.Trim()}\". Make it a solid line of 2px or more, so that it shows around every control.");

        // The colour of the text, which is the furthest from the page in either theme.
        Assert.True(
            parts.Contains(TextColour, StringComparer.OrdinalIgnoreCase),
            $"The outline of :focus-visible is \"{outline.Groups[1].Value.Trim()}\", in a colour that this test cannot say the value of " +
            $"on a light and on a dark page. Draw it in {TextColour}, which is the colour of the text in either theme.");

        AssertReads("The outline of :focus-visible", TextOnLight, Colours.LightPage, "light");
        AssertReads("The outline of :focus-visible", TextOnDark, Colours.DarkPage, "dark");
    }

    [Fact]
    public async Task An_outline_is_taken_away_only_where_the_list_says()
    {
        var taken = (await RulesAsync())
            .Where(rule => NoOutline().IsMatch(rule.Declarations))
            .Select(rule => rule.Selector)
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = taken
            .Where(selector => !AccessibilityRules.WithoutOutline.ContainsKey(selector))
            .Order(StringComparer.Ordinal)
            .Select(selector =>
                $"A stylesheet takes the outline away from {selector}, so nothing shows that it has the focus of the keyboard. " +
                "Leave it its outline, or list the selector in AccessibilityRules.WithoutOutline with why.");

        var stale = AccessibilityRules.WithoutOutline.Keys
            .Where(selector => !taken.Contains(selector))
            .Select(selector =>
                $"AccessibilityRules.WithoutOutline lists {selector}, and no rule of the stylesheets takes its outline away any more. " +
                "Remove the entry.");

        var wrong = unlisted.Concat(stale).ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public async Task The_line_around_a_control_reads_on_the_page_of_each_theme()
    {
        var rules = await RulesAsync();

        // The list holds something, so the loop looks at something.
        Assert.NotEmpty(AccessibilityRules.ControlBorders);

        foreach (var selector in AccessibilityRules.ControlBorders.Keys)
        {
            var ofTheControl = rules.Where(rule => rule.Selector == selector).ToList();

            Assert.True(
                ofTheControl.Count > 0,
                $"AccessibilityRules.ControlBorders lists {selector}, and no rule of the stylesheets is for it any more. " +
                "Remove the entry, or give it the selector that the control has now.");

            var border = ofTheControl.Select(rule => MixedBorder().Match(rule.Declarations)).FirstOrDefault(border => border.Success);

            Assert.True(
                border is not null,
                $"{selector} does not draw its border with color-mix(in srgb, currentColor N%, transparent) any more, so this test " +
                "cannot say how it reads. Draw it that way, or in a colour of the system, and take the selector off the list.");

            var share = double.Parse(border.Groups[1].Value, CultureInfo.InvariantCulture) / 100;
            var line = string.Create(CultureInfo.InvariantCulture, $"The line around {selector}, which is {share * 100:0.#} percent of the colour of the text,");

            AssertReads(line, Colours.Over(TextOnLight, share, Colours.LightPage), Colours.LightPage, "light");
            AssertReads(line, Colours.Over(TextOnDark, share, Colours.DarkPage), Colours.DarkPage, "dark");
        }
    }

    [Theory]
    [InlineData("#000000", 0.4, "#ffffff", "#999999")]
    [InlineData("#000000", 0.5, "#ffffff", "#808080")]
    [InlineData("#ffffff", 0.5, "#1e1e1e", "#8f8f8f")]
    [InlineData("#b3261e", 1, "#ffffff", "#b3261e")]
    [InlineData("#b3261e", 0, "#1e1e1e", "#1e1e1e")]
    public void A_colour_that_is_mixed_with_nothing_is_that_much_of_itself_on_the_page(string colour, double share, string page, string seen)
    {
        Assert.Equal(seen, Colours.Over(colour, share, page));
    }

    /// <summary>Asserts that what shows a control, or where the keyboard is, reads on the page of a theme.</summary>
    private static void AssertReads(string what, string colour, string page, string theme)
    {
        var contrast = Colours.Contrast(colour, page);

        Assert.True(
            contrast >= ForControls,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{what} is {colour} on a {theme} page, with a contrast of {contrast:0.##} to 1. What shows a control takes {ForControls} to 1 (WCAG 1.4.11)."));
    }

    /// <summary>
    /// Every rule of the stylesheets that a page names, as the application serves them: each
    /// selector of a rule with what the rule declares. A rule inside a condition, such as a
    /// width of the screen, is among them, without its condition.
    /// </summary>
    private static async Task<IReadOnlyList<(string Selector, string Declarations)>> RulesAsync()
    {
        var rules = new List<(string, string)>();

        foreach (var (_, styles) in await Stylesheets.AllAsync())
        {
            foreach (Match rule in Rule().Matches(Stylesheets.WithoutComments(styles)))
            {
                foreach (var selector in rule.Groups[1].Value.Split(','))
                {
                    // Without the mark that the styles beside a component carry, which is new with every build.
                    rules.Add((Space().Replace(Scope().Replace(selector, string.Empty), " ").Trim(), rule.Groups[2].Value));
                }
            }
        }

        return rules;
    }

    // A rule: its selectors, and what it declares. One that holds rules is not one itself.
    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex Rule();

    [GeneratedRegex(@"\[b-[a-z0-9]+\]")]
    private static partial Regex Scope();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    // The outline that a rule declares, whole.
    [GeneratedRegex(@"(?<![-\w])outline\s*:\s*([^;}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Outline();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)px$", RegexOptions.IgnoreCase)]
    private static partial Regex Pixels();

    // An outline that is none, or as wide as nothing.
    [GeneratedRegex(@"(?<![-\w])outline(?:-style|-width)?\s*:\s*(?:none|0(?:px|rem|em)?)\s*(?:!important\s*)?(?:;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NoOutline();

    // A border that is drawn in a share of the colour of the text, over whatever is behind it.
    [GeneratedRegex(@"(?<![-\w])border(?:-[a-z-]+)?\s*:[^;}]*color-mix\(in srgb,\s*currentColor\s+(\d+(?:\.\d+)?)%,\s*transparent\)", RegexOptions.IgnoreCase)]
    private static partial Regex MixedBorder();
}
