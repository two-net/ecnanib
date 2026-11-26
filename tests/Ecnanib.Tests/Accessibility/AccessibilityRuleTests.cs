using System.Reflection;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Microsoft.AspNetCore.Components;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// Each screen passes an accessibility check before release (NFR-USA-1), so each screen is on
/// the list that the checks go through. These tests hold the pages of the application to the
/// list of <see cref="AccessibilityRules.States"/>, in both directions, without starting it: a
/// page that is added has a state before it is checked by anything, and a state names a page
/// that there is. See docs/accessibility.md.
/// </summary>
public partial class AccessibilityRuleTests
{
    [Fact]
    public void Every_page_has_a_state_on_the_list()
    {
        var listed = AccessibilityRules.States.Select(state => state.Route).ToHashSet(StringComparer.Ordinal);

        var unlisted = Routes()
            .Where(route => !listed.Contains(route))
            .Select(route =>
                $"The page at {route} has no state in AccessibilityRules.States, so nothing checks it. " +
                "Add each state of it that looks different to a user.")
            .ToList();

        Assert.True(unlisted.Count == 0, string.Join(Environment.NewLine, unlisted));
    }

    [Fact]
    public void No_state_names_a_page_that_is_gone()
    {
        var routes = Routes().ToHashSet(StringComparer.Ordinal);

        var gone = AccessibilityRules.States
            .Where(state => !routes.Contains(state.Route))
            .Select(state =>
                $"AccessibilityRules.States lists \"{state.State}\" of {state.Route}, and no page has that route any more. " +
                "Remove the entry, or give it the route the page has now.")
            .ToList();

        Assert.True(gone.Count == 0, string.Join(Environment.NewLine, gone));
    }

    [Fact]
    public void Every_state_fills_the_parameters_of_its_route()
    {
        var unfilled = new List<string>();

        foreach (var state in AccessibilityRules.States)
        {
            // What is left in braces once the world has put in what it stands for.
            if (Parameter().IsMatch(Filled(AddressOf(state))))
            {
                unfilled.Add(
                    $"The route {state.Route} has a parameter that the state \"{state.State}\" does not fill. " +
                    "Say in its entry what a path of it is.");
            }

            // The address of a state is one that its route answers, but for a page that is shown in place of another.
            if (state.How is not Reach.Standing && !PathsOf(state.Route).IsMatch(AddressOf(state).Split('?')[0]))
            {
                unfilled.Add(
                    $"AccessibilityRules.States gives \"{state.State}\" of {state.Route} the path {state.Path}, " +
                    "which is no path of that route. Give it a path of the page, or the route of the page that the path is of.");
            }
        }

        Assert.True(unfilled.Count == 0, string.Join(Environment.NewLine, unfilled));

        // The parameters that stand for a token stand for one that the world fills.
        Assert.All(AccessibilityRules.Defaults.Values, token => Assert.Contains(token.Trim('{', '}'), AccessibilityRules.Tokens));
    }

    [Fact]
    public void Every_state_is_listed_once_and_says_what_shows_it_and_why_it_is_listed()
    {
        var twice = AccessibilityRules.States
            .GroupBy(state => (state.Route, state.State, state.As))
            .Where(same => same.Count() > 1)
            .Select(same => $"AccessibilityRules.States lists \"{same.Key.State}\" of {same.Key.Route}, as {same.Key.As}, {same.Count()} times.")
            .ToList();

        Assert.True(twice.Count == 0, string.Join(Environment.NewLine, twice));

        Assert.All(AccessibilityRules.States, state =>
        {
            Assert.False(string.IsNullOrWhiteSpace(state.State), $"A state of {state.Route} has no name. Say in a few words what it is.");
            Assert.False(
                string.IsNullOrWhiteSpace(state.Shows),
                $"The state \"{state.State}\" of {state.Route} does not say what shows it. Give it a selector of what makes the state.");
            Assert.False(
                string.IsNullOrWhiteSpace(state.Why),
                $"The state \"{state.State}\" of {state.Route} does not say why it is listed. Say what a user finds different in it.");

            // A selector that a page as it is read and a page in a browser answer alike: one
            // that asks what is checked or focused now is answered by the browser alone.
            Assert.False(
                state.Shows.Contains(':', StringComparison.Ordinal),
                $"The state \"{state.State}\" of {state.Route} is shown by {state.Shows}. Keep to elements, classes, ids, and attributes.");
        });
    }

    [Fact]
    public void Nothing_of_anyone_else_stands_after_the_states_that_take_a_code()
    {
        // A code moves the clock of the world on, and a proof of who someone is holds for ten
        // minutes: the states of the others stand before the clock moves.
        var states = AccessibilityRules.States.ToList();
        var first = states.FindIndex(TakesACode);

        Assert.True(first >= 0, "No state takes a code of two-factor authentication any more, so this test holds nothing.");

        var after = states
            .Skip(first)
            .Where(state => state.As is not (Who.Guarded or Who.AtTheCode))
            .Select(state =>
                $"The state \"{state.State}\" of {state.Route}, as {state.As}, stands after a state that takes a code, " +
                "which moves the clock on. Put it before the states of two-factor authentication.")
            .ToList();

        Assert.True(after.Count == 0, string.Join(Environment.NewLine, after));

        // And never five wrong codes in a row, which would have every code refused.
        var wrongInARow = 0;

        foreach (var state in states.Where(TakesACode))
        {
            wrongInARow = Entered(state).Contains(AccessibilityRules.WrongCode) ? wrongInARow + 1 : 0;

            Assert.True(
                wrongInARow < TwoFactorRules.WrongCodesAllowed,
                $"The state \"{state.State}\" of {state.Route} is wrong code number {wrongInARow} in a row, with which every code of " +
                "the account is refused. Put a state that enters a right code before it.");
        }
    }

    [Fact]
    public void Every_exception_says_why()
    {
        Assert.All(AccessibilityRules.LeftOff, state => Assert.False(
            string.IsNullOrWhiteSpace(state.Value), $"\"{state.Key}\" is left off the list of the states without a word of why."));

        Assert.All(AccessibilityRules.NotOfTheUser, field => Assert.False(
            string.IsNullOrWhiteSpace(field.Value), $"The field {field.Key} is listed as no password of the user without a word of why."));

        Assert.All(AccessibilityRules.WithoutOutline, selector => Assert.False(
            string.IsNullOrWhiteSpace(selector.Value), $"{selector.Key} is listed as without an outline without a word of why."));

        Assert.All(AccessibilityRules.ControlBorders, selector => Assert.False(
            string.IsNullOrWhiteSpace(selector.Value), $"{selector.Key} is listed as the line of a control without a word of why."));

        // Each rule of the markup is named once, and says what it serves and what holds.
        Assert.Equal(AccessibilityRules.Markup.Count, AccessibilityRules.Markup.Select(rule => rule.Id).Distinct().Count());
        Assert.All(AccessibilityRules.Markup, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Criterion), $"The rule \"{rule.Id}\" does not say which criterion it serves.");
            Assert.False(string.IsNullOrWhiteSpace(rule.Holds), $"The rule \"{rule.Id}\" does not say what a page that keeps to it is like.");
        });
    }

    /// <summary>The route of every page of the application, as the page says it.</summary>
    private static IEnumerable<string> Routes() =>
        typeof(Program).Assembly.GetTypes()
            .SelectMany(component => component.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Distinct()
            .Order(StringComparer.Ordinal);

    /// <summary>The address that a state is at, as the list says it: its own path, or its route.</summary>
    private static string AddressOf(ScreenState state) =>
        state.How is Reach.Standing standing ? standing.Address : state.Path ?? state.Route;

    /// <summary>An address without what the world puts in: the tokens, and the parameters that stand for one.</summary>
    private static string Filled(string address)
    {
        foreach (var parameter in AccessibilityRules.Defaults.Keys)
        {
            address = address.Replace(parameter, string.Empty, StringComparison.Ordinal);
        }

        foreach (var token in AccessibilityRules.Tokens)
        {
            address = address.Replace($"{{{token}}}", string.Empty, StringComparison.Ordinal);
        }

        return address;
    }

    /// <summary>The paths that a route answers: the route, with anything where it has a parameter.</summary>
    private static Regex PathsOf(string route) =>
        new($"^{Parameter().Replace(Regex.Escape(route).Replace("\\{", "{", StringComparison.Ordinal), "[^/]+")}$", RegexOptions.CultureInvariant);

    private static bool TakesACode(ScreenState state) =>
        Entered(state).Any(value => value is AccessibilityRules.Code or AccessibilityRules.WrongCode);

    /// <summary>What a state enters in the form that it answers.</summary>
    private static IEnumerable<string> Entered(ScreenState state) =>
        state.How is Reach.Post answered ? answered.Fields.Select(field => field.Value) : [];

    // What a route, or a path, has in braces.
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Parameter();
}
