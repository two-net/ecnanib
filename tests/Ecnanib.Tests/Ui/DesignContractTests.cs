using System.Globalization;
using System.Text.RegularExpressions;
using Ecnanib.Tests.Support;

namespace Ecnanib.Tests.Ui;

/// <summary>
/// The stylesheet side of NFR-USE-01 and NFR-USE-02, as rules the tree must keep: every colour is a token and every pair
/// the design uses meets WCAG 2.1 AA in both schemes (4.5:1 for text, 3:1 for the focus ring, control borders and the chart),
/// the focus ring is never removed, the fallback palette for browsers without light-dark() is the light one, forced colours
/// are handled, and motion has one mechanism: every transition and finite animation reads --ui-duration, every infinite one
/// lives under an .anim-loop element, both reduced-motion signals zero the one and stop the other, and the scripts that
/// animate check both signals too.
/// </summary>
public class DesignContractTests
{
    private static readonly string TokensPath = Path.Combine(SourceTree.ApplicationRoot, "wwwroot", "css", "tokens.css");

    private static readonly Regex Pair = new(@"--(?<name>[a-z0-9-]+):\s*light-dark\((?<light>#[0-9a-fA-F]{6}),\s*(?<dark>#[0-9a-fA-F]{6})\);", RegexOptions.Compiled);

    private static readonly Regex Plain = new(@"--(?<name>[a-z0-9-]+):\s*(?<value>#[0-9a-fA-F]{6});", RegexOptions.Compiled);

    /// <summary>A declaration whose value holds a colour literal; selectors (<c>#blazor-error-ui</c>) do not match because a declaration starts with a property name and a colon.</summary>
    private static readonly Regex ColourLiteral = new(@"(?m)^\s*[a-z-]+\s*:\s*[^;{}]*?(#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(|\boklch\(|\blight-dark\()", RegexOptions.Compiled);

    private static readonly Regex Declaration = new(@"(?<property>\b(transition|animation|animation-duration|animation-delay)\b)\s*:\s*(?<value>[^;{}]+);", RegexOptions.Compiled);

    /// <summary>The elements that carry <c>.anim-loop</c> in the markup, so an infinite animation under one of these selectors is governed.</summary>
    private static readonly string[] LoopSelectors = [".council-scene", ".pulse", ".role-", ".token", ".outcome", ".components-rejoining-animation", ".evaluating", ".role-state"];

    private static IReadOnlyList<string> Stylesheets() =>
        Directory.EnumerateFiles(Path.Combine(SourceTree.ApplicationRoot, "wwwroot", "css"), "*.css")
            .Concat(Directory.EnumerateFiles(Path.Combine(SourceTree.ApplicationRoot, "Components"), "*.razor.css", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, (string Light, string Dark)> Tokens() =>
        Pair.Matches(File.ReadAllText(TokensPath)).ToDictionary(m => m.Groups["name"].Value, m => (m.Groups["light"].Value, m.Groups["dark"].Value), StringComparer.Ordinal);

    /// <summary>Text on the backgrounds it is placed on (1.4.3): 4.5:1 or better in both schemes.</summary>
    [Theory]
    [InlineData("text", "bg")]
    [InlineData("text", "surface")]
    [InlineData("text", "surface-2")]
    [InlineData("text", "accent-bg")]
    [InlineData("text", "ok-bg")]
    [InlineData("text", "warn-bg")]
    [InlineData("text-muted", "bg")]
    [InlineData("text-muted", "surface")]
    [InlineData("text-muted", "surface-2")]
    [InlineData("text-muted", "accent-bg")]
    [InlineData("text-muted", "ok-bg")]
    [InlineData("text-muted", "danger-bg")]
    [InlineData("accent", "bg")]
    [InlineData("accent", "surface")]
    [InlineData("accent", "accent-bg")]
    [InlineData("accent-hover", "bg")]
    [InlineData("accent-hover", "surface")]
    [InlineData("accent-text", "accent")]
    [InlineData("accent-text", "accent-hover")]
    [InlineData("ok", "ok-bg")]
    [InlineData("ok", "bg")]
    [InlineData("ok", "surface")]
    [InlineData("warn", "warn-bg")]
    [InlineData("warn", "bg")]
    [InlineData("warn", "surface")]
    [InlineData("danger", "danger-bg")]
    [InlineData("danger", "bg")]
    [InlineData("danger", "surface")]
    [InlineData("danger-text", "danger")]
    public void TextContrastIsAtLeastFourAndAHalfInBothSchemes(string foreground, string background) => AssertContrast(foreground, background, 4.5);

    /// <summary>The focus ring, the boundary of inputs and buttons, and the chart's candles and markers (1.4.11): 3:1 or better.</summary>
    [Theory]
    [InlineData("focus", "bg")]
    [InlineData("focus", "surface")]
    [InlineData("focus", "surface-2")]
    [InlineData("focus", "accent-bg")]
    [InlineData("border-strong", "surface")]
    [InlineData("border-strong", "bg")]
    [InlineData("accent", "surface-2")]
    [InlineData("chart-up", "surface")]
    [InlineData("chart-down", "surface")]
    [InlineData("chart-marker-exit", "surface")]
    [InlineData("chart-marker-stop", "surface")]
    public void NonTextContrastIsAtLeastThreeInBothSchemes(string foreground, string background) => AssertContrast(foreground, background, 3.0);

    /// <summary>Without light-dark() the page shows the light palette: every pair has a fallback and it is the pair's light value.</summary>
    [Fact]
    public void TheFallbackPaletteIsTheLightOne()
    {
        var text = File.ReadAllText(TokensPath);
        var header = "@supports not (color: light-dark(#000, #fff)) {";
        var start = text.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "tokens.css has no @supports not (color: light-dark(…)) block.");
        var body = text[(start + header.Length)..];
        body = body[..body.IndexOf("\n}\n", StringComparison.Ordinal)];
        var fallback = Plain.Matches(body).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value, StringComparer.Ordinal);
        var pairs = Tokens();

        Assert.Equal(pairs.Keys.Order(StringComparer.Ordinal), fallback.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, (light, _)) in pairs)
            Assert.True(string.Equals(light, fallback[name], StringComparison.OrdinalIgnoreCase), $"--{name}: fallback {fallback[name]} is not the light value {light}.");
        Assert.DoesNotContain("light-dark(", body, StringComparison.Ordinal);
    }

    /// <summary>Every colour the stylesheets use is a token in tokens.css, so the contrast checks above cover the whole design.</summary>
    [Fact]
    public void EveryColourIsATokenInTokensCss()
    {
        var violations = new List<string>();
        foreach (var file in Stylesheets().Where(f => !f.EndsWith("tokens.css", StringComparison.Ordinal)))
        {
            foreach (Match match in ColourLiteral.Matches(File.ReadAllText(file)))
                violations.Add($"{SourceTree.RelativeToApplication(file)}: {match.Value.Trim()}");
        }

        Assert.Empty(violations);
    }

    /// <summary>2.4.7: one :focus-visible ring, never removed (the programmatic focus on the h1 is the one exception), nothing reset with all: unset, placeholders in a token colour.</summary>
    [Fact]
    public void TheFocusRingIsNeverRemoved()
    {
        var baseCss = File.ReadAllText(Path.Combine(SourceTree.ApplicationRoot, "wwwroot", "css", "base.css"));
        Assert.Matches(@":focus-visible\s*\{\s*outline:\s*2px solid var\(--focus\);", baseCss);

        var removals = new List<string>();
        foreach (var file in Stylesheets())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("all: unset", text, StringComparison.Ordinal);
            foreach (Match match in Regex.Matches(text, @"(?<selector>[^{}]+)\{[^{}]*?outline\s*:\s*(none|0)\b"))
            {
                var selector = Regex.Replace(match.Groups["selector"].Value, @"/\*.*?\*/", "", RegexOptions.Singleline).Trim();
                if (selector != "h1:focus")
                    removals.Add($"{SourceTree.RelativeToApplication(file)}: {selector}");
            }
        }

        Assert.Empty(removals);
        var components = File.ReadAllText(Path.Combine(SourceTree.ApplicationRoot, "wwwroot", "css", "components.css"));
        Assert.Matches(@"::placeholder\s*\{\s*color:\s*var\(--text-muted\);\s*opacity:\s*1;", components);
        Assert.Contains("@media (forced-colors: active)", components, StringComparison.Ordinal);
    }

    /// <summary>NFR-USE-02: both signals zero --ui-duration and stop .anim-loop (element, descendants and pseudo-elements) with !important, so nothing can turn motion back on.</summary>
    [Fact]
    public void BothReducedMotionSignalsStopEverything()
    {
        var tokens = File.ReadAllText(TokensPath);

        var media = Regex.Match(tokens, @"@media \(prefers-reduced-motion: reduce\)\s*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}");
        Assert.True(media.Success, "tokens.css has no prefers-reduced-motion block.");
        AssertStops(media.Groups["body"].Value, ":root");

        var attribute = tokens[tokens.IndexOf(":root[data-motion=\"reduced\"]", StringComparison.Ordinal)..];
        AssertStops(attribute, ":root[data-motion=\"reduced\"]");

        static void AssertStops(string css, string root)
        {
            Assert.Matches(Regex.Escape(root) + @"\s*\{\s*--ui-duration:\s*0ms;", css);
            foreach (var selector in new[] { ".anim-loop", ".anim-loop *", ".anim-loop::before", ".anim-loop::after", ".anim-loop *::before", ".anim-loop *::after" })
                Assert.Contains(selector, css, StringComparison.Ordinal);
            Assert.Contains("animation: none !important;", css, StringComparison.Ordinal);
        }
    }

    /// <summary>FR-UI-03, NFR-USE-02: every transition and finite animation reads --ui-duration (so none outlasts the 400 ms cap and reduced motion zeroes it); every infinite one is under an .anim-loop element.</summary>
    [Fact]
    public void EveryTransitionAndAnimationIsGovernedByTheToken()
    {
        var violations = new List<string>();
        foreach (var file in Stylesheets())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Declaration.Matches(text))
            {
                var property = match.Groups["property"].Value;
                var value = match.Groups["value"].Value.Trim();
                var governed = value.Contains("var(--ui-duration)", StringComparison.Ordinal) || value.StartsWith("none", StringComparison.Ordinal);
                var infinite = property == "animation" && value.Contains("infinite", StringComparison.Ordinal);
                var selector = SelectorOf(text, match.Index);
                if (infinite || (property == "animation-delay" && LoopSelectors.Any(selector.Contains)))
                {
                    // A decorative loop (or the phase offset of one): governed by the .anim-loop rule, so its element must be one.
                    if (!LoopSelectors.Any(s => selector.Contains(s, StringComparison.Ordinal)))
                        violations.Add($"{SourceTree.RelativeToApplication(file)}: infinite animation outside an .anim-loop element: {selector} {{ {property}: {value} }}");
                }
                else if (!governed)
                {
                    violations.Add($"{SourceTree.RelativeToApplication(file)}: {selector} {{ {property}: {value} }}");
                }
            }
        }

        Assert.Empty(violations);

        // The markup gives those elements the class the stylesheet relies on.
        foreach (var (file, fragment) in new[]
        {
            ("Components/Pages/Hero.razor", "class=\"anim-loop council-scene\""),
            ("Components/Layout/ReconnectModal.razor", "class=\"components-rejoining-animation anim-loop\""),
            ("Components/Live/DashboardLive.razor", "class=\"evaluating anim-loop\""),
            ("Components/Live/AgentHeaderLive.razor", "class=\"evaluating anim-loop\""),
            ("Components/Live/DecisionsLive.razor", "class=\"evaluating anim-loop\""),
            ("Components/Live/DeliberationSequence.razor", "class=\"role-state anim-loop"),
        })
            Assert.Contains(fragment, File.ReadAllText(Path.Combine(SourceTree.ApplicationRoot, file)), StringComparison.Ordinal);
    }

    /// <summary>The scripts that move things check both signals: the replay skips to its end, the chart neither animates its scroll nor keeps kinetic scrolling.</summary>
    [Fact]
    public void TheScriptsThatAnimateCheckBothSignals()
    {
        foreach (var file in new[] { "Components/Live/PriceChart.razor.js", "Components/Live/DeliberationReplay.razor.js" })
        {
            var script = File.ReadAllText(Path.Combine(SourceTree.ApplicationRoot, file));
            Assert.Contains("prefers-reduced-motion: reduce", script, StringComparison.Ordinal);
            Assert.Contains("dataset.motion === \"reduced\"", script, StringComparison.Ordinal);
        }

        var chart = File.ReadAllText(Path.Combine(SourceTree.ApplicationRoot, "Components", "Live", "PriceChart.razor.js"));
        Assert.Contains("scrollToPosition(0, false)", chart, StringComparison.Ordinal);
        Assert.Contains("kineticScroll: { touch: !reduced, mouse: false }", chart, StringComparison.Ordinal);
        // 1.4.1: direction is shape as well as colour.
        Assert.Contains("upColor: \"transparent\"", chart, StringComparison.Ordinal);
        Assert.Contains("borderVisible: true", chart, StringComparison.Ordinal);
    }

    /// <summary>2.2.1 and 1.4.4 in the markup: no page reloads itself unasked, and the viewport never forbids zoom.</summary>
    [Fact]
    public void NoPageReloadsItselfUnaskedAndZoomIsNeverForbidden()
    {
        foreach (var file in SourceTree.ApplicationSources(".razor"))
        {
            var text = File.ReadAllText(file);
            var relative = SourceTree.RelativeToApplication(file);
            if (text.Contains("http-equiv=\"refresh\"", StringComparison.Ordinal))
            {
                Assert.Equal("Components/Health/Pages/Status.razor", relative);
                Assert.Contains("@if (_reload)", text, StringComparison.Ordinal);
            }

            Assert.DoesNotMatch(@"<th\b[^>]*>\s*</th>", text);
        }

        var app = File.ReadAllText(Path.Combine(SourceTree.ApplicationRoot, "Components", "App.razor"));
        Assert.Contains("<html lang=\"en\"", app, StringComparison.Ordinal);
        var viewport = Regex.Match(app, @"<meta name=""viewport"" content=""(?<content>[^""]*)""").Groups["content"].Value;
        Assert.DoesNotContain("user-scalable", viewport, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maximum-scale", viewport, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertContrast(string foreground, string background, double minimum)
    {
        var tokens = Tokens();
        Assert.True(tokens.ContainsKey(foreground), $"--{foreground} is not a light-dark() token.");
        Assert.True(tokens.ContainsKey(background), $"--{background} is not a light-dark() token.");
        var light = Contrast(tokens[foreground].Light, tokens[background].Light);
        var dark = Contrast(tokens[foreground].Dark, tokens[background].Dark);

        Assert.True(light >= minimum, $"--{foreground} on --{background} (light): {light:0.00}:1, needs {minimum}:1.");
        Assert.True(dark >= minimum, $"--{foreground} on --{background} (dark): {dark:0.00}:1, needs {minimum}:1.");
    }

    /// <summary>WCAG 2.1 contrast ratio of two sRGB colours.</summary>
    internal static double Contrast(string first, string second)
    {
        var (a, b) = (Luminance(first), Luminance(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var c = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static string SelectorOf(string css, int declarationIndex)
    {
        var open = css.LastIndexOf('{', declarationIndex);
        var close = css.LastIndexOf('}', open);
        return Regex.Replace(css[(close + 1)..open], @"/\*.*?\*/", "", RegexOptions.Singleline).Trim();
    }
}
