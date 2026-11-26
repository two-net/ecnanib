namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// The texts of the two pages that switch the mode of an agent, in one place (NFR-USA-4): the
/// one that switches it to Live, where it trades with real money, and takes the confirmation of
/// the risk, and the one that switches it back to Demo (FR-AGT-7, NFR-LEG-1). What the pages of
/// an agent say of its mode wherever they are, the risks of Live among it, is in
/// <see cref="AgentPages"/>.
/// </summary>
public static class AgentModePages
{
    /// <summary>The title of the page that switches an agent to Live, with the name of the agent.</summary>
    public static string ToLiveTitle(string agentName) => $"Switch the agent {agentName} to Live";

    /// <summary>What the page says first, before the state of the agent, which the switch does not change.</summary>
    public static string LiveExplained =>
        "In Live the agent trades in your real Binance account, with your Binance key for Live. Its state stays as it is:";

    /// <summary>What it says after that state and what the state means: switching the mode starts nothing.</summary>
    public static string NothingUntilStarted => "It trades with real money only once you start it.";

    /// <summary>What stands before the link to the risk disclosure, under the risks of Live.</summary>
    public static string RiskInFullBefore => "The";

    /// <summary>What stands after that link.</summary>
    public static string RiskInFull => "says this in full.";

    /// <summary>The label of the box that a user ticks to confirm the risk of trading with real money (FR-AGT-7).</summary>
    public static string ConfirmRiskLabel =>
        "I understand that in Live this agent trades with real money in my Binance account, and that I can lose all of the " +
        "capital I allocated to it.";

    /// <summary>What the page says in place of its form for an agent that is in Live already.</summary>
    public static string AlreadyLive => "This agent is in Live.";

    /// <summary>The link back to the page of the state, which switches nothing.</summary>
    public static string KeepInDemo => "Keep the agent in Demo";

    /// <summary>The title of the page that switches an agent back to Demo, with the name of the agent.</summary>
    public static string ToDemoTitle(string agentName) => $"Switch the agent {agentName} to Demo";

    /// <summary>What the page says first, before the state of the agent, which the switch does not change.</summary>
    public static string DemoExplained =>
        "In Demo the agent trades in Binance's test environment, with your Binance key for Demo, and no money is at stake. " +
        "Its state stays as it is:";

    /// <summary>What it says after that state: what the agent did in Live keeps its mode.</summary>
    public static string LiveStaysLabeled => "What it did in Live stays labeled as Live.";

    /// <summary>What it says of switching the agent to Live once more.</summary>
    public static string BackToLiveExplained =>
        "Switching it to Live again takes your confirmation of the risk, and a fresh proof of who you are.";

    /// <summary>What the page says in place of its form for an agent that is in Demo already.</summary>
    public static string AlreadyDemo => "This agent is in Demo.";

    /// <summary>The link back to the page of the state, which switches nothing.</summary>
    public static string KeepInLive => "Keep the agent in Live";
}
