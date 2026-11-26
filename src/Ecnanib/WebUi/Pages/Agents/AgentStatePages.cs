using System.Globalization;
using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Account;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// The texts of the pages that say and change the state of an agent, in one place (NFR-USA-4):
/// the page of its state, which is the screen of the agent, the page that asks once more before
/// it is started, paused, or resumed, and the pages that stop it, delete it, and close its open
/// position (FR-AGT-10, FR-AGT-11, FR-AGT-12, FR-UI-3). What a state, its cause, and a control
/// are called, and what the pages of an agent have in common, is in <see cref="AgentPages"/>.
/// </summary>
public static class AgentStatePages
{
    /// <summary>The title of the page of the state of an agent, with the name of the agent.</summary>
    public static string StateTitle(string agentName) => $"State of the agent {agentName}";

    /// <summary>
    /// What the page of the state says beside the badge of the mode, before the symbol of the
    /// agent, which it writes as code.
    /// </summary>
    public static string AgentTrades => "The agent trades";

    /// <summary>
    /// What follows the symbol there: the market of the agent, and where it trades in its mode.
    /// The page ends the sentence.
    /// </summary>
    public static string WhereItTrades(Market market, TradingMode mode) =>
        $"on {AgentPages.NameOf(market)} in {KeyPages.Describe(mode)}";

    /// <summary>The heading of what the service has to tell the user about the agent, and they have not read.</summary>
    public static string NotificationsHeading => "Notifications";

    /// <summary>What the page says under that heading.</summary>
    public static string NotificationsExplained => "What the service has to tell you about this agent, latest first.";

    /// <summary>
    /// What follows the state of the agent: since when it is in it. The page writes the state
    /// before it, and the time is written as every page writes one.
    /// </summary>
    /// <param name="time">When the agent came to its state, as a page writes a time.</param>
    public static string Since(string time) => $"since {time}";

    /// <summary>The heading of the controls that start, pause, resume, and stop the agent, and close its position.</summary>
    public static string ControlsHeading => "Controls";

    /// <summary>What the page says above the control that closes the open position of the agent (FR-AGT-12).</summary>
    public static string ClosePositionExplained =>
        "The agent has an open position. You can close it at any time, at the market price, in whatever state the agent is.";

    /// <summary>What the page says of the two modes for an agent in Demo, the mode that it is in first.</summary>
    public static string ModesExplainedInDemo =>
        "In Demo the agent trades in Binance's test environment, and no money is at stake. In Live it trades in your real " +
        "Binance account, with real money.";

    /// <summary>What the page says of the two modes for an agent in Live, the mode that it is in first.</summary>
    public static string ModesExplainedInLive =>
        "In Live the agent trades in your real Binance account, with real money. In Demo it trades in Binance's test " +
        "environment, and no money is at stake.";

    /// <summary>The heading of the changes of the state of the agent, and the caption of their table (FR-AGT-10).</summary>
    public static string ChangesHeading => "State changes";

    /// <summary>What the page says above the changes when it shows the latest of them, and not all.</summary>
    /// <param name="shown">How many changes the page shows.</param>
    /// <param name="total">How many changes the agent has.</param>
    public static string LatestChanges(int shown, int total) =>
        $"The latest {shown.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} changes, latest first.";

    /// <summary>What the page says above the changes when it shows them all.</summary>
    public static string AllChanges => "Every change of the agent's state, latest first.";

    /// <summary>The heading of the column of the changes that holds when each happened.</summary>
    public static string WhenHeading => "When";

    /// <summary>The heading of the column of the changes that holds why each happened.</summary>
    public static string WhyHeading => "Why";

    /// <summary>
    /// The link back to the page of the state, from a page that asks once more before the
    /// state of an agent is changed: following it changes nothing.
    /// </summary>
    public static string KeepAsItIs => "Keep the agent as it is";

    /// <summary>The title of the page that stops an agent, with the name of the agent.</summary>
    public static string StopTitle(string agentName) => $"Stop the agent {agentName}";

    /// <summary>What the choices of what happens to the open position of an agent that is stopped are a choice of (FR-AGT-11).</summary>
    public static string PositionLegend => "The agent has an open position. What happens to it?";

    /// <summary>The label of the choice that closes the position.</summary>
    public static string CloseAtMarketLabel => "Close the position at market";

    /// <summary>What that choice does, as the hint under it.</summary>
    public static string CloseAtMarketHint =>
        "The position is closed now, at the market price, and its stop-loss and take-profit orders are taken off Binance. " +
        "A profit or a loss is final.";

    /// <summary>The label of the choice that keeps the position.</summary>
    public static string KeepPositionLabel => "Keep the position with its protective orders";

    /// <summary>What that choice does, as the hint under it, with the control that closes the position later.</summary>
    public static string KeepPositionHint =>
        "The position stays open at Binance with its stop-loss and take-profit orders, which close it when the price " +
        "reaches one of them. The agent no longer manages it. You can close it at any time with \"" +
        AgentPages.ClosePositionNow + "\".";

    /// <summary>What the page that stops an agent says in place of the choice, for an agent that has no open position.</summary>
    public static string NoOpenPosition => "The agent has no open position.";

    /// <summary>What the form that stops an agent says when it came without the choice, and the agent has a position.</summary>
    public static string ChoosePositionFirst => "This agent has an open position. Choose what happens to it.";

    /// <summary>What the page says when the agent was stopped, and its position could not be closed.</summary>
    public static string StoppedNotClosed => $"The agent is stopped. {AgentPages.NotClosed}";

    /// <summary>What the page that stops an agent says of an agent that is stopped already.</summary>
    public static string AlreadyStopped => "This agent is stopped.";

    /// <summary>The title of the page that deletes an agent, with the name of the agent.</summary>
    public static string DeleteTitle(string agentName) => $"Delete the agent {agentName}";

    /// <summary>What the page that deletes an agent says before it asks once more.</summary>
    public static string DeleteExplained => "This deletes the agent and its settings, at once and for good. It cannot be undone.";

    /// <summary>The button that deletes the agent.</summary>
    public static string Delete => "Delete the agent";

    /// <summary>The link back to the list of the agents, which deletes nothing.</summary>
    public static string KeepAgent => "Keep the agent";

    /// <summary>What the page says when the agent is not deleted because it is not stopped.</summary>
    public static string StopFirst => "This agent is not stopped. Stop it before you delete it.";

    /// <summary>What the page says when the agent is not deleted because it holds a position or orders.</summary>
    public static string CloseFirst => "This agent has an open position or open orders. Close them before you delete it.";

    /// <summary>The title of the page that closes the open position of an agent, with the name of the agent.</summary>
    public static string ClosePositionTitle(string agentName) => $"Close the position of the agent {agentName}";

    /// <summary>
    /// What that page says when the position was closed, with the state of the agent, which is
    /// as it was, and what that state means (NFR-USA-2).
    /// </summary>
    public static string Closed(AgentState state) =>
        $"The position was closed at market. The agent is {AgentPages.Describe(state)}, as it was. {AgentPages.Explain(state)}";

    /// <summary>What that page says before it asks once more: what closing the position does.</summary>
    public static string CloseExplained =>
        "This closes the agent's open position now, at the market price, and takes its stop-loss and take-profit orders " +
        "off Binance. A profit or a loss is final, and it cannot be undone.";

    /// <summary>What stands before the state of the agent there, which closing the position does not change.</summary>
    public static string StateStays => "The state of the agent stays as it is:";

    /// <summary>The link back to the page of the state, which closes nothing.</summary>
    public static string KeepPositionLink => "Keep the position";
}
