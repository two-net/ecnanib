namespace Ecnanib.WebUi.Layout;

/// <summary>
/// The texts of the dialog that says a page is rejoining the server, in one place (NFR-USA-4):
/// what it says while the connection of a page that follows what changes is down, and what its
/// buttons say. The framework shows one sentence of it at a time. The name of the dialog, as a
/// screen reader says it, is <see cref="Screens.ServerConnection"/>. See docs/live-updates.md.
/// </summary>
public static class ConnectionPages
{
    /// <summary>What the dialog says while the page joins the server again for the first time.</summary>
    public static string Rejoining => "Rejoining the server...";

    /// <summary>What it says when that did not work, before it says in how many seconds it tries again.</summary>
    public static string RejoinFailed => "Rejoin failed... trying again";

    /// <summary>
    /// What stands before the seconds that count down, with the blanks around it. The blanks
    /// are of the text, and not of the page: the part that holds them begins with them, and a
    /// page writes no blank where a part begins.
    /// </summary>
    public static string TriesAgainIn => " in ";

    /// <summary>What stands after those seconds, with the blank before it.</summary>
    public static string Seconds => " seconds";

    /// <summary>What it says when the page could not join the server again at all.</summary>
    public static string NotRejoined => "Failed to rejoin.";

    /// <summary>What it says under that, and under a session that could not be resumed: what its user can do.</summary>
    public static string RetryOrReload => "Please retry or reload the page.";

    /// <summary>What it says when the server paused the session of the page.</summary>
    public static string Paused => "The session has been paused by the server.";

    /// <summary>What it says when a paused session could not be resumed.</summary>
    public static string NotResumed => "Failed to resume the session.";

    /// <summary>The button that tries to join the server again.</summary>
    public static string Retry => "Retry";

    /// <summary>The button that resumes a paused session.</summary>
    public static string Resume => "Resume";
}
