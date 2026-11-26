namespace Ecnanib.WebUi.Pages.Operator;

/// <summary>
/// The texts that frame the operator status screen, and those of the page of the global halt,
/// in one place (NFR-USA-4). The screen has a route and a place in the navigation of an operator
/// (FR-UI-2). What it shows has its texts in <see cref="OperatorStatusPages"/> (FR-OPS-2). It
/// does nothing itself: it leads to the pages on which an operator does something, of which the
/// global halt is one (FR-OPS-3). See "Operators" in docs/ownership.md.
/// </summary>
public static class OperatorPages
{
    /// <summary>The name of the form that switches the global halt on.</summary>
    public const string HaltOnForm = "halt-on";

    /// <summary>The name of the form that switches the global halt off.</summary>
    public const string HaltOffForm = "halt-off";

    /// <summary>Whom the screen is for.</summary>
    public static string ForOperators =>
        "This screen is for operators: the users whose email addresses the configuration file lists.";

    /// <summary>
    /// That the screen only shows, said over the links to the pages on which an operator does
    /// something: nothing of it is an action, so nothing of it is recorded (FR-OPS-5).
    /// </summary>
    public static string OnlyShows =>
        "It shows nothing of any user, and nothing can be done on it. What an operator does is on a page of its own:";

    /// <summary>
    /// The title, in the browser, of the page of the global halt, its heading, and the text of
    /// the link that leads to it from the operator status screen.
    /// </summary>
    public static string HaltTitle => "Global halt";

    /// <summary>What the global halt does, in the words that a rejected plan is explained with.</summary>
    public static string HaltExplained =>
        "While the global halt is on, no agent opens a new position. Open positions are still managed and protected.";

    /// <summary>
    /// Whom the halt acts on, said before the button: it is no setting of the operator's own
    /// agents, and it reaches those that trade with real money.
    /// </summary>
    public static string HaltActsOnEveryone =>
        "The halt acts on the agents of every user, and on those in Live, which trade with real money.";

    /// <summary>That the halt is off, as the page says it over the button that switches it on.</summary>
    public static string HaltIsOff => "The global halt is off. Agents open positions as their teams decide.";

    /// <summary>That the halt is on, and since when, as the page says it over the button that switches it off.</summary>
    /// <param name="since">When the halt was switched on.</param>
    /// <param name="times">How the page writes a time for whoever reads it.</param>
    public static string HaltIsOn(DateTimeOffset since, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);

        return $"The global halt is on, since {times.At(since)}.";
    }

    /// <summary>What switching the halt on does, and what it does not, before the button.</summary>
    public static string HaltOnMeans =>
        "Switching it on holds at once. A plan that opens a position is rejected, and an entry order that still rests on " +
        "the exchange is taken off within a minute. It closes no position, and it stops no agent.";

    /// <summary>What switching the halt off does, before the button.</summary>
    public static string HaltOffMeans => "Switching it off lets agents open positions again, from their next decision on.";

    /// <summary>The text of the button that switches the halt on.</summary>
    public static string HaltOn => "Switch the global halt on";

    /// <summary>The text of the button that switches the halt off.</summary>
    public static string HaltOff => "Switch the global halt off";

    /// <summary>That a switch is recorded, said under the button (FR-OPS-5).</summary>
    public static string HaltRecorded => "Each switch is recorded in the audit trail, with who made it and when.";

    /// <summary>The text of the link that leads from the page of the halt back to the operator status screen.</summary>
    public static string BackToStatus => "Back to the operator status";
}
