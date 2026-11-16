using Ecnanib.Credentials;

namespace Ecnanib.Agents;

/// <summary>
/// What a user does to the state of an agent: starting, pausing, resuming, and stopping it
/// (FR-AGT-1), with what happens to its open position when it stops (FR-AGT-11), and closing its
/// position at any time (FR-AGT-12). Every change of a state is recorded with its cause and its
/// time, and read here (FR-AGT-10). See docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// Every operation is for the user who is signed in on the request, and reaches the agents of
/// that user only. An agent of another user is not found (NFR-SEC-3).
/// </para>
/// <para>
/// A control is offered in the states that <see cref="AgentStates.After"/> names. A form that is
/// sent twice finds the agent where the first one left it, and is answered as the first one was.
/// </para>
/// </remarks>
public interface IAgentControls
{
    /// <summary>
    /// Starts an agent that is a draft, or is stopped: it runs. A Binance credential that is
    /// missing, is not valid, or lacks the permission of the agent's market blocks the start
    /// (FR-KEY-5), and so does another active agent of the user for the same credential, market,
    /// and symbol (FR-AGT-9).
    /// </summary>
    Task<AgentControlResult> StartAsync(HttpContext request, Guid agentId);

    /// <summary>
    /// Pauses an agent that runs: it runs no cycle and opens no position. An open position stays
    /// open with its protective orders.
    /// </summary>
    Task<AgentControlResult> PauseAsync(HttpContext request, Guid agentId);

    /// <summary>
    /// Resumes an agent that is paused, is halted, or requires attention: it runs again. What
    /// blocks a start as far as the credential goes blocks this as well.
    /// </summary>
    Task<AgentControlResult> ResumeAsync(HttpContext request, Guid agentId);

    /// <summary>
    /// Stops an agent that was started. When it has an open position, its user chooses whether
    /// the position is closed at market or kept with its protective orders, and an agent that
    /// has one is not stopped without the choice (FR-AGT-11). The agent is stopped first, and
    /// the position is closed after: a position that could not be closed is that of a stopped
    /// agent, with its protective orders.
    /// </summary>
    /// <param name="request">The request of the signed-in user.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="choice">What happens to an open position, or null when nothing was chosen.</param>
    Task<StopAgentResult> StopAsync(HttpContext request, Guid agentId, PositionAtStop? choice);

    /// <summary>
    /// Closes the open position of an agent at market, now, in whatever state the agent is
    /// (FR-AGT-12). The state of the agent stays as it is.
    /// </summary>
    Task<ClosePositionOutcome> ClosePositionAsync(HttpContext request, Guid agentId);

    /// <summary>
    /// The changes of the state of an agent, latest first, each with its cause and its time
    /// (FR-AGT-10), or null when the user has no agent of that id.
    /// </summary>
    Task<AgentStateHistory?> HistoryAsync(HttpContext request, Guid agentId);
}

/// <summary>How starting, pausing, or resuming an agent ended.</summary>
public enum AgentControlOutcome
{
    /// <summary>The agent is in the state that the control leads to: by this request, or by one before it.</summary>
    Done,

    /// <summary>The user has no agent of that id.</summary>
    NotFound,

    /// <summary>The control is not offered in the state that the agent is in. Nothing changed.</summary>
    NotOffered,

    /// <summary>The Binance credential of the user keeps the agent from running (FR-KEY-5). Nothing changed.</summary>
    KeyBlocks,

    /// <summary>
    /// The user has another active agent for the same Binance credential, market, and symbol
    /// (FR-AGT-9). Nothing changed.
    /// </summary>
    AnotherAgentActive,
}

/// <summary>What starting, pausing, or resuming an agent gave.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Block">What about the credential keeps the agent from running, when that is how it ended.</param>
public sealed record AgentControlResult(AgentControlOutcome Outcome, StartBlock? Block = null);

/// <summary>What happens to the open position of an agent that is stopped, as its user chooses (FR-AGT-11).</summary>
public enum PositionAtStop
{
    /// <summary>The position is closed at market.</summary>
    Close,

    /// <summary>The position stays open with its protective orders.</summary>
    Keep,
}

/// <summary>How stopping an agent ended.</summary>
public enum StopAgentOutcome
{
    /// <summary>The agent is stopped: by this request, or by one before it.</summary>
    Stopped,

    /// <summary>The agent has an open position, and nothing was chosen for it. Nothing changed.</summary>
    ChoiceNeeded,

    /// <summary>The agent is a draft: it was never started. Nothing changed.</summary>
    NotOffered,

    /// <summary>The user has no agent of that id.</summary>
    NotFound,
}

/// <summary>What stopping an agent gave.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Close">
/// How closing the position ended, when the agent was stopped by this request and its user chose
/// to close its position. Null otherwise.
/// </param>
public sealed record StopAgentResult(StopAgentOutcome Outcome, PositionCloseOutcome? Close = null);

/// <summary>How closing the position of an agent by hand ended.</summary>
public enum ClosePositionOutcome
{
    /// <summary>The position is closed, or the order that closes it is with the exchange.</summary>
    Closed,

    /// <summary>The agent has no open position. Nothing was asked of the exchange.</summary>
    NothingOpen,

    /// <summary>The position could not be closed. It is open as it was, with its protective orders.</summary>
    Failed,

    /// <summary>The user has no agent of that id.</summary>
    NotFound,
}

/// <summary>The changes of the state of an agent, as its user may read them.</summary>
/// <param name="Changes">The latest changes, latest first: <see cref="MostShown"/> at most.</param>
/// <param name="Total">How many changes are recorded for the agent.</param>
public sealed record AgentStateHistory(IReadOnlyList<AgentStateChangeView> Changes, int Total)
{
    /// <summary>The most changes that are read at once.</summary>
    public const int MostShown = 50;
}

/// <summary>One change of the state of an agent.</summary>
/// <param name="State">The state the agent came to.</param>
/// <param name="Cause">Why it did.</param>
/// <param name="ChangedAt">When it did.</param>
public sealed record AgentStateChangeView(AgentState State, AgentStateCause Cause, DateTimeOffset ChangedAt);
