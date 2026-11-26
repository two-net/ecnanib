namespace Ecnanib.WebUi.Pages.Operator;

/// <summary>
/// The texts of the operator status screen, in one place (NFR-USA-4). The screen has a route and
/// a place in the navigation of an operator (FR-UI-2). What it is to show is not built yet
/// (FR-OPS-2), and it does nothing: see "Operators" in docs/ownership.md.
/// </summary>
public static class OperatorPages
{
    /// <summary>Whom the screen is for.</summary>
    public static string ForOperators =>
        "This screen is for operators: the users whose email addresses the configuration file lists.";

    /// <summary>What the screen is to show, and does not yet.</summary>
    public static string NotBuiltYet =>
        "What it is to show is not built yet: the health of the scheduler, the depth of the queue, the last catalog sync of " +
        "each provider, the use of the request limits of Binance, the drift of the clock, the sizes of the tables, and the " +
        "rates of errors.";

    /// <summary>What the screen holds for now, which is what makes it safe to have before the rest.</summary>
    public static string OnlyShows => "It shows nothing of any user, and nothing can be done on it.";
}
