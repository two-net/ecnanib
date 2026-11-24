using System.Globalization;
using System.Text.RegularExpressions;

namespace Ecnanib.Tests.Themes;

/// <summary>
/// The team at work on the agent screen is an animated pipeline (FR-ANI-1), and when the device
/// asks for reduced motion it changes state without non-essential animation (FR-ANI-4). What
/// moves is in the stylesheet of the pages alone: no page carries a style or a script for it.
/// These tests hold that stylesheet to what the pipeline rests on: that nothing moves for a
/// device that asks for reduced motion, that a change is short, that the one thing which moves
/// for as long as its state lasts is of a role that works, and that only how far something
/// shows and how large it is drawn are changed (FR-ANI-5, FR-ANI-6). See "The team at work" in
/// docs/agent-screen.md.
/// </summary>
/// <remarks>
/// No test drives a browser: that the pipeline looks right is seen by hand, as
/// docs/agent-screen.md says. What is held here are the rules of the stylesheet of the pages,
/// as the application serves it. The dialog that rejoins the server is the framework's own,
/// with a stylesheet of its own, and is not held to them.
/// </remarks>
public partial class MotionStyleTests
{
    // What a device that asks for no reduced motion is asked: what moves is inside it.
    private const string ForMotion = "@media (prefers-reduced-motion: no-preference)";

    // The least and the most that a change takes (FR-ANI-5).
    private static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(400);

    // What a browser changes without laying anything out again, and without painting anything
    // but the layer that it is of (FR-ANI-6).
    private static readonly string[] Moved = ["opacity", "transform"];

    [Fact]
    public async Task Nothing_moves_for_a_device_that_asks_for_reduced_motion()
    {
        var (motion, rest) = await MotionAsync();

        // Whatever moves is said for a device that asks for no reduced motion, and nowhere
        // else: on a device that asks for it, a state changes at once.
        Assert.NotEmpty(Moving().Matches(motion));
        Assert.Empty(Moving().Matches(rest).Select(moving => $"{Stylesheets.RuleOf(rest, moving.Index)} {{ {moving.Value.Trim()} }}"));

        // And no rule asks for reduced motion to take something back: there is nothing to.
        Assert.DoesNotContain("prefers-reduced-motion: reduce", rest, StringComparison.Ordinal);

        // What the pipeline shows without moving is outside: the state of a role, and the
        // line into a stage that the work was handed to, are there for every device.
        Assert.Matches(@"\.role-working \.role-marker::before\s*\{", rest);
        Assert.Matches(@"\.pipeline-stage \+ \.pipeline-stage\.stage-handed::after\s*\{\s*transform:\s*none;", rest);
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
                $"{Stylesheets.RuleOf(motion, change.Index)} changes in {time.TotalMilliseconds} ms: {change.Value.Trim()}"));
        });

        // The hand-off is one of them: the line into a stage is drawn through as the work is handed over.
        Assert.Contains(changes, change => Stylesheets.RuleOf(motion, change.Index) == ".pipeline-stage + .pipeline-stage::after");
    }

    [Fact]
    public async Task What_moves_for_as_long_as_its_state_lasts_is_of_a_role_that_works_and_comes_and_goes_once_a_second_at_the_most()
    {
        var (motion, _) = await MotionAsync();

        var animations = Animation().Matches(motion);

        // Continuous animation is used only for a state that is live: a role that works is the
        // one. Nothing flashes more than three times per second: it comes and goes once in a
        // second, or less often.
        var lasting = Assert.Single(animations, animation => animation.Groups[1].Value.Contains("infinite", StringComparison.Ordinal));

        Assert.Equal(".role-working .role-marker::before", Stylesheets.RuleOf(motion, lasting.Index));
        Assert.True(TimeOf(Time().Match(lasting.Groups[1].Value)) >= TimeSpan.FromSeconds(1), lasting.Value);

        // An animation that ends is a change, and is as short as one.
        Assert.All(
            animations.Where(animation => animation != lasting),
            animation => Assert.InRange(TimeOf(Time().Match(animation.Groups[1].Value)), Shortest, Longest));
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

        // And so do the steps of what moves for as long as its state lasts.
        var steps = Keyframes().Matches(motion);

        Assert.NotEmpty(steps);
        Assert.All(steps, moving =>
            Assert.All(
                Declared().Matches(moving.Groups["steps"].Value).Select(declared => declared.Groups[1].Value),
                property => Assert.Contains(property, Moved)));
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

    /// <summary>A time as a stylesheet writes it: <c>300ms</c>, <c>1.5s</c>.</summary>
    private static TimeSpan TimeOf(Match time)
    {
        Assert.True(time.Success, "A time was expected.");

        var number = double.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);

        return time.Groups[2].Value == "ms" ? TimeSpan.FromMilliseconds(number) : TimeSpan.FromSeconds(number);
    }

    // Whatever has something move: a change, an animation, and the steps of one.
    [GeneratedRegex(@"(?<![-\w])(?:transition|animation)(?:-[a-z-]+)?\s*:[^;}]*|@keyframes\s+[\w-]+")]
    private static partial Regex Moving();

    [GeneratedRegex(@"(?<![-\w])transition\s*:([^;}]*)")]
    private static partial Regex Transition();

    [GeneratedRegex(@"(?<![-\w])animation\s*:([^;}]*)")]
    private static partial Regex Animation();

    [GeneratedRegex(@"(?<![\w.])(\d*\.?\d+)(ms|s)\b")]
    private static partial Regex Time();

    // The steps of an animation: each a rule of its own inside it.
    [GeneratedRegex(@"@keyframes\s+[\w-]+\s*\{(?<steps>(?:[^{}]*\{[^{}]*\})*)\s*\}")]
    private static partial Regex Keyframes();

    [GeneratedRegex(@"(?<=[{;]\s*)([a-z-]+)\s*:")]
    private static partial Regex Declared();
}
