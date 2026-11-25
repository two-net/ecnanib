using System.Globalization;
using System.Text.RegularExpressions;
using Ecnanib.Tests.Themes;

namespace Ecnanib.Tests.Animation;

/// <summary>
/// What moves on the pages is in the stylesheet of the pages alone: the team at work
/// (FR-ANI-1), a figure that counts and the equity curve that unfolds (FR-ANI-2), a page, a
/// state, an item of a list, and the dialog that rejoins the server (FR-ANI-3). No page carries
/// a style for it, and the script of the pages says when something moves and never how. These
/// tests hold the stylesheet to what all of it rests on: that nothing moves for a device that
/// asks for reduced motion, nor for an account that reduced it (FR-ANI-4), that a change is
/// short, that what moves for as long as its state lasts is of a state that is live and stands
/// still in a tab that nobody sees (FR-ANI-5, FR-ANI-6), and that only how far something shows
/// and how large it is drawn are changed (FR-ANI-6). See docs/motion.md.
/// </summary>
/// <remarks>
/// No test drives a browser: that what moves looks right is seen by hand, as docs/motion.md
/// says. What is held here are the rules of the stylesheets, as the application serves them.
/// </remarks>
public partial class MotionStyleTests
{
    // What a device that asks for no reduced motion is asked: what moves is inside it.
    private const string ForMotion = "@media (prefers-reduced-motion: no-preference)";

    // What a rule inside it says of the page first: that its account did not reduce what moves.
    private const string NotReduced = ":root:not([data-motion=\"reduced\"])";

    // The least and the most that a change takes (FR-ANI-5).
    private static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(400);

    // What a browser changes without laying anything out again, and without painting anything
    // but the layer that it is of (FR-ANI-6).
    private static readonly string[] Moved = ["opacity", "transform"];

    // What moves for as long as its state lasts, each of a state that is live (FR-ANI-5): a
    // role that works, and the server that is being rejoined.
    private static readonly string[] Lasting =
    [
        $"{NotReduced} .components-rejoining-animation div",
        $"{NotReduced} .role-working .role-marker::before",
    ];

    [Fact]
    public async Task Nothing_moves_for_a_device_that_asks_for_reduced_motion()
    {
        var (motion, rest) = await MotionAsync();

        // Whatever moves is said for a device that asks for no reduced motion, and nowhere
        // else: on a device that asks for it, a state changes at once.
        Assert.NotEmpty(Moving().Matches(motion));
        Assert.Empty(Moving().Matches(rest).Select(moving => $"{RuleOf(rest, moving.Index)} {{ {moving.Value.Trim()} }}"));

        // And no rule asks for reduced motion to take something back: there is nothing to.
        Assert.DoesNotContain("prefers-reduced-motion: reduce", rest, StringComparison.Ordinal);

        // What the pipeline shows without moving is outside: the state of a role, and the
        // line into a stage that the work was handed to, are there for every device.
        Assert.Matches(@"\.role-working \.role-marker::before\s*\{", rest);
        Assert.Matches(@"\.pipeline-stage \+ \.pipeline-stage\.stage-handed::after\s*\{\s*transform:\s*none;", rest);

        // And so is the placeholder of the content: it is a look, and comes for every device.
        Assert.Matches(@"main\[aria-busy=""true""\]::before\s*\{", rest);
    }

    [Fact]
    public async Task Nothing_moves_for_an_account_that_reduced_it()
    {
        var (motion, _) = await MotionAsync();

        var rules = Rule().Matches(Keyframes().Replace(motion, string.Empty));

        // Every rule of what moves says of the page first that its account did not reduce it,
        // each of its selectors by itself: so a page that says "reduced" is given none of them,
        // whatever its device asks.
        Assert.NotEmpty(rules);
        Assert.All(
            rules.SelectMany(rule => rule.Groups["selectors"].Value.Split(',')).Select(Spaced),
            selector => Assert.True(
                selector == NotReduced || selector.StartsWith(NotReduced + " ", StringComparison.Ordinal)
                    || selector.StartsWith(NotReduced + "[", StringComparison.Ordinal),
                $"A rule moves something for an account that reduced it: {selector}"));
    }

    [Fact]
    public async Task What_moves_is_in_the_stylesheet_of_the_pages_alone()
    {
        // The other stylesheet is the one of the styles beside a component, which holds the
        // look of the dialog that rejoins the server. What moves of the dialog is with
        // everything else that moves, so one stylesheet is held to the rules of it.
        var pages = await Stylesheets.OfThePagesAsync();

        foreach (var (name, styles) in (await Stylesheets.AllAsync()).Where(sheet => sheet.Styles != pages))
        {
            var others = Stylesheets.WithoutComments(styles);

            Assert.Empty(Moving().Matches(others).Select(moving => $"{name}: {RuleOf(others, moving.Index)} {{ {moving.Value.Trim()} }}"));
            Assert.DoesNotContain("prefers-reduced-motion", others, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_change_takes_between_150_and_400_milliseconds()
    {
        var (motion, _) = await MotionAsync();

        var changes = Transition().Matches(motion);

        Assert.NotEmpty(changes);
        Assert.All(changes, change =>
        {
            var times = Time().Matches(change.Groups[1].Value).Select(TimeOf).ToList();

            Assert.NotEmpty(times);
            Assert.All(times, time => Assert.True(
                time >= Shortest && time <= Longest,
                $"{RuleOf(motion, change.Index)} changes in {time.TotalMilliseconds} ms: {change.Value.Trim()}"));
        });

        // The hand-off is one of them: the line into a stage is drawn through as the work is handed over.
        Assert.Contains(changes, change => RuleOf(motion, change.Index) == $"{NotReduced} .pipeline-stage + .pipeline-stage::after");

        // An animation that ends is a change, and is as short as one: the first time that it
        // says is how long it takes. The dialog alone waits before it, for as long again.
        var ending = Animated(motion).Where(animation => !animation.Lasts).ToList();

        Assert.NotEmpty(ending);
        Assert.All(ending, animation => Assert.True(
            animation.Takes >= Shortest && animation.Takes <= Longest,
            $"{animation.Rule} moves for {animation.Takes.TotalMilliseconds} ms."));
        Assert.Equal(
            [$"{NotReduced} #components-reconnect-modal[open]"],
            ending.Where(animation => animation.Waits > TimeSpan.Zero).Select(animation => animation.Rule));
        Assert.All(ending, animation => Assert.InRange(animation.Waits, TimeSpan.Zero, Longest));
    }

    [Fact]
    public async Task What_moves_for_as_long_as_its_state_lasts_is_of_a_state_that_is_live_and_comes_and_goes_once_a_second_at_the_most()
    {
        var (motion, _) = await MotionAsync();

        var lasting = Animated(motion).Where(animation => animation.Lasts).ToList();

        // Continuous animation is used only for a state that is live: a role that works, and
        // the server that is being rejoined. A placeholder does not move by itself, nor does
        // anything else.
        Assert.Equal(Lasting, lasting.Select(animation => animation.Rule).Order(StringComparer.Ordinal));

        // Nothing flashes more than three times per second: each comes and goes once in a
        // second, or less often.
        Assert.All(lasting, animation => Assert.True(animation.Takes >= TimeSpan.FromSeconds(1), animation.Rule));

        // And each stands still in a tab that nobody sees, which the script says of the page,
        // and the role also while its pipeline is off the screen (FR-ANI-6).
        var paused = Rule().Matches(Keyframes().Replace(motion, string.Empty))
            .Where(rule => Regex.IsMatch(rule.Groups["body"].Value, @"animation-play-state:\s*paused"))
            .SelectMany(rule => rule.Groups["selectors"].Value.Split(',').Select(Spaced))
            .ToList();

        Assert.All(lasting, animation => Assert.Contains(animation.Rule.Replace(NotReduced, NotReduced + "[data-hidden]"), paused));
        Assert.Contains($"{NotReduced} [data-offscreen] .role-working .role-marker::before", paused);
    }

    [Fact]
    public async Task Only_how_far_something_shows_and_how_large_it_is_drawn_are_changed()
    {
        var (motion, _) = await MotionAsync();

        // A change names what it changes, and never everything.
        Assert.All(Transition().Matches(motion), change =>
            Assert.All(
                change.Groups[1].Value.Split(',').Select(part => part.Trim().Split(' ')[0]),
                property => Assert.Contains(property, Moved)));

        // And so do the steps of every animation.
        var steps = Keyframes().Matches(motion);

        Assert.NotEmpty(steps);
        Assert.All(steps, moving =>
            Assert.All(
                Declared().Matches(moving.Groups["steps"].Value).Select(declared => declared.Groups[1].Value),
                property => Assert.Contains(property, Moved)));
    }

    [Fact]
    public async Task An_animation_has_its_steps_beside_it_and_no_steps_are_left_over()
    {
        var (motion, _) = await MotionAsync();

        var named = Animated(motion).Select(animation => animation.Name).Distinct().Order(StringComparer.Ordinal).ToList();
        var steps = Keyframes().Matches(motion).Select(moving => moving.Groups["name"].Value).Order(StringComparer.Ordinal).ToList();

        // An animation whose steps are not there moves nothing, and says nothing of it.
        Assert.Equal(steps, named);
        Assert.Equal(steps.Distinct(), steps);
    }

    [Fact]
    public async Task The_times_that_the_script_reads_are_said_by_the_stylesheet()
    {
        var (motion, rest) = await MotionAsync();

        // How long a figure counts is a change, as short as one, and is given with what
        // moves: a page that is given none of that has no such time, and its figures change
        // at once.
        var counts = Assert.Single(Property("--figure-change").Matches(motion));

        Assert.Equal(NotReduced, RuleOf(motion, counts.Index));
        Assert.InRange(TimeOf(Time().Match(counts.Groups[1].Value)), Shortest, Longest);
        Assert.DoesNotContain("--figure-change", rest, StringComparison.Ordinal);

        // How long a page may be on its way before the placeholder shows is no motion, and
        // holds for every device: a placeholder that came at once would flash on every page.
        var waits = Assert.Single(Property("--loading-after").Matches(rest));

        Assert.Equal(":root", RuleOf(rest, waits.Index));
        Assert.InRange(TimeOf(Time().Match(waits.Groups[1].Value)), Shortest, Longest);
        Assert.DoesNotContain("--loading-after", motion, StringComparison.Ordinal);

        // And so does how long the placeholder stays at the most: a page that never comes
        // gives the content back, on every device, after seconds and not after minutes.
        var stays = Assert.Single(Property("--loading-longest").Matches(rest));

        Assert.Equal(":root", RuleOf(rest, stays.Index));
        Assert.InRange(TimeOf(Time().Match(stays.Groups[1].Value)), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
        Assert.DoesNotContain("--loading-longest", motion, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stylesheet of the pages in two: what it says for a device that asks for no reduced
    /// motion, and the rest of it.
    /// </summary>
    private static async Task<(string Motion, string Others)> MotionAsync()
    {
        var styles = Stylesheets.WithoutComments(await Stylesheets.OfThePagesAsync());
        var motion = new List<string>();

        for (var at = styles.IndexOf(ForMotion, StringComparison.Ordinal); at >= 0; at = styles.IndexOf(ForMotion, at, StringComparison.Ordinal))
        {
            // To the brace that closes the one which the question opens.
            var opened = styles.IndexOf('{', at);
            var closed = opened;

            for (var depth = 1; depth > 0;)
            {
                closed = styles.IndexOfAny(['{', '}'], closed + 1);
                depth += styles[closed] == '{' ? 1 : -1;
            }

            motion.Add(styles[(opened + 1)..closed]);
            styles = styles.Remove(at, closed + 1 - at);
        }

        Assert.NotEmpty(motion);

        return (string.Join(Environment.NewLine, motion), styles);
    }

    /// <summary>
    /// The animations that a part of a stylesheet gives, each with the rule that gives it, the
    /// name of its steps, how long it takes, how long it waits first, and whether it goes on
    /// for as long as its rule holds. A rule that gives none, to have something stand still,
    /// is not one.
    /// </summary>
    private static List<(string Rule, string Name, TimeSpan Takes, TimeSpan Waits, bool Lasts)> Animated(string styles) =>
    [
        .. Animation().Matches(styles)
            .Where(animation => animation.Groups[1].Value.Trim() != "none")
            .Select(animation =>
            {
                var said = animation.Groups[1].Value.Trim();
                var times = Time().Matches(said).Select(TimeOf).ToList();

                Assert.True(times.Count is 1 or 2, $"An animation says how long it takes, and how long it waits at the most: {said}");

                return (
                    RuleOf(styles, animation.Index),
                    said.Split(' ')[0],
                    times[0],
                    times.Count == 2 ? times[1] : TimeSpan.Zero,
                    said.Split(' ').Contains("infinite"));
            }),
    ];

    /// <summary>The selector of the rule that a place in a stylesheet is in, on one line.</summary>
    private static string RuleOf(string styles, int index) => Spaced(Stylesheets.RuleOf(styles, index));

    /// <summary>A selector with one space wherever the stylesheet has room.</summary>
    private static string Spaced(string selector) => Room().Replace(selector.Trim(), " ");

    /// <summary>A time as a stylesheet writes it: <c>300ms</c>, <c>1.5s</c>.</summary>
    private static TimeSpan TimeOf(Match time)
    {
        Assert.True(time.Success, "A time was expected.");

        var number = double.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);

        return time.Groups[2].Value == "ms" ? TimeSpan.FromMilliseconds(number) : TimeSpan.FromSeconds(number);
    }

    /// <summary>What a stylesheet gives a custom property of a name.</summary>
    private static Regex Property(string name) => new($@"(?<![-\w]){Regex.Escape(name)}\s*:([^;}}]*)");

    // Whatever has something move: a change, an animation, and the steps of one.
    [GeneratedRegex(@"(?<![-\w])(?:transition|animation)(?:-[a-z-]+)?\s*:[^;}]*|@keyframes\s+[\w-]+")]
    private static partial Regex Moving();

    [GeneratedRegex(@"(?<![-\w])transition\s*:([^;}]*)")]
    private static partial Regex Transition();

    [GeneratedRegex(@"(?<![-\w])animation\s*:([^;}]*)")]
    private static partial Regex Animation();

    [GeneratedRegex(@"(?<![\w.-])(\d*\.?\d+)(ms|s)\b")]
    private static partial Regex Time();

    // The steps of an animation: each a rule of its own inside it.
    [GeneratedRegex(@"@keyframes\s+(?<name>[\w-]+)\s*\{(?<steps>(?:[^{}]*\{[^{}]*\})*)\s*\}")]
    private static partial Regex Keyframes();

    // A rule: its selectors, and what it declares.
    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex Rule();

    [GeneratedRegex(@"(?<=[{;]\s*)([a-z-]+)\s*:")]
    private static partial Regex Declared();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Room();
}
