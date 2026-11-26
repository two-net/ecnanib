using System.Globalization;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.WebUi.Pages.Experience;

/// <summary>
/// The texts of the page of the platform-wide board, in one place (NFR-USA-4, FR-EXP-10): what
/// the board is and what it never shows, which mode it shows and why, when a record is shown,
/// and what a user reads about leaving the board and coming back to it. A record is written as
/// on the experience screen, by <see cref="ExperiencePages"/>, under the same headings. See "The
/// platform-wide board" in docs/experience.md.
/// </summary>
/// <remarks>
/// No text of the page says a number of trades that is not of a record which is shown: how many
/// trades there are of a mode, or of a record that too few contribute to, is said nowhere.
/// </remarks>
public static class BoardPages
{
    /// <summary>The name of the board, in its heading and where a page leads to it.</summary>
    public static string Title => "Platform-wide board";

    /// <summary>What the experience screen leads to the board with.</summary>
    public static string Link => "Platform-wide board: how the trades of all users came out";

    /// <summary>What the board leads to the experience screen with.</summary>
    public static string OwnRecordsLink => "Your own experience records";

    /// <summary>What the board says first: what it is, and what its records are measured in.</summary>
    public static string Explained =>
        "This is how the trades of the agents of all users came out for each AI provider and each model, added up across users. "
        + ExperiencePages.InRExplained;

    /// <summary>What the board follows, which says why a trade can leave it.</summary>
    public static string Follows =>
        "The board is worked out from the positions that are stored, each time it is shown. When an agent or an account is deleted, "
        + "its trades leave the board, and so do the trades of a user who leaves it.";

    /// <summary>The heading of what a user reads and chooses about their own trades.</summary>
    public static string ContributionHeading => "Your trades on the board";

    /// <summary>What a user who contributes reads.</summary>
    public static string Contributing =>
        "The closed trades of your agents are counted on the board: in records like the ones here, and never under your name. "
        + "You can leave the board. None of your trades is then counted in it, from the next time it is shown.";

    /// <summary>What a user who left the board reads.</summary>
    public static string LeftOut =>
        "You have left the board: none of your trades is counted in it. You can come back to it at any time.";

    /// <summary>What leaving changes nothing of, which a user reads either way.</summary>
    public static string LeavingKeeps =>
        "Leaving the board changes nothing of your own experience records, and you still see the board.";

    /// <summary>The button that leaves the board.</summary>
    public static string Leave => "Leave the board";

    /// <summary>The button that comes back to the board.</summary>
    public static string Return => "Count my trades on the board again";

    /// <summary>The name of the form that leaves the board.</summary>
    public const string LeaveForm = "leave-board";

    /// <summary>The name of the form that comes back to the board.</summary>
    public const string ReturnForm = "return-to-board";

    /// <summary>The path of the board with a filter, which a link there keeps. It names its filter as the experience screen does.</summary>
    public static string Path(TrackRecordFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return WebUiModule.BoardPath + ExperiencePages.QueryOf(filter, withRole: true);
    }

    /// <summary>
    /// What the board never shows, and when a record is shown (FR-EXP-10): only when at least
    /// the users and the trades of the configuration file contribute to it.
    /// </summary>
    public static string NeverShows(BoardScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return "The board shows no user, no balance, and no single trade. "
            + $"A provider or a model has a record here only when at least {Enough(scope)} contribute to it: "
            + "a record that fewer contribute to is not shown, and neither is how many trades it would count.";
    }

    /// <summary>
    /// Which mode the board shows, and why (FR-EXP-9): the one that was asked for, or Live where
    /// it has a record to show, and otherwise Demo. It says no number of trades.
    /// </summary>
    public static string ModeExplained(BoardScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return (scope.ModeChosen, scope.Mode) switch
        {
            (false, _) => $"These are the results of all users in {AgentPages.NameOf(scope.Mode)}, which you chose.",
            (true, TradingMode.Live) =>
                "These are the results of all users in Live: Live has a record that enough users and trades contribute to.",
            (true, _) =>
                "These are the results of all users in Demo: Live has no record that enough users and trades contribute to.",
        };
    }

    /// <summary>The link to the results of the mode that the board does not show. It says no number of trades.</summary>
    public static string OtherModeLink(BoardScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return $"Show the results of all users in {AgentPages.NameOf(ExperiencePages.OtherThan(scope.Mode))}";
    }

    /// <summary>For whom a trade counts on the board, which says why its rows do not add up: as on the experience screen.</summary>
    public static string CountsFor(Role? role) => role is { } chosen
        ? ExperiencePages.CountsFor(chosen)
        : "A trade counts once for every model of the team that decided it, and once for every provider of that team. "
            + "So the rows do not add up to a number of trades, and a record says how the teams with a model did, and not what the model did alone.";

    /// <summary>The name of the table of the records of the providers, which says the mode that they are of.</summary>
    public static string ProvidersCaption(TradingMode mode) => $"The track record of each provider across all users in {AgentPages.NameOf(mode)}";

    /// <summary>The name of the table of the records of the models.</summary>
    public static string ModelsCaption(TradingMode mode) => $"The track record of each model across all users in {AgentPages.NameOf(mode)}";

    /// <summary>What the board says when no provider has a record that may be shown.</summary>
    public static string NoProviders(BoardScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return $"No provider has a record here in {AgentPages.NameOf(scope.Mode)}: to none of them do at least {Enough(scope)} contribute.";
    }

    /// <summary>What the board says when no model has a record that may be shown.</summary>
    public static string NoModels(BoardScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return $"No model has a record here in {AgentPages.NameOf(scope.Mode)}: to none of them do at least {Enough(scope)} contribute.";
    }

    /// <summary>What is enough for a record to be shown, in words: <c>5 users and 50 closed trades</c>.</summary>
    private static string Enough(BoardScope scope) =>
        $"{Users(scope.MinUsers)} and {ExperiencePages.Trades(scope.MinTrades)}";

    private static string Users(int count) =>
        count == 1 ? "1 user" : $"{count.ToString(CultureInfo.InvariantCulture)} users";
}
