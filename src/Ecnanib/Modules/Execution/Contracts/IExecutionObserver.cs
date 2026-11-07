namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// Hears that an agent's orders, position or trades changed, once the change has committed (FR-UI-05): an entry sent
/// or filled, a stop placed or moved, a part closed, a trade booked or settled, or an open trade's fees and funding
/// read. The Web UI subscribes for live updates, and reads again through <see cref="IAgentPositions"/> and
/// <see cref="ITradeJournal"/>. It says only which agent, and may come several times for one pass. Register an
/// implementation as a singleton. It's an in-process event, lost on a crash, so a page reads when it opens. See Live
/// updates in docs/architecture.md.
/// </summary>
public interface IExecutionObserver
{
    /// <summary>
    /// Called on the thread that made the change, which may be protective work, so it must return at once and never
    /// throw. One that throws is logged, and stops neither the work nor the other observers. It hears every user's
    /// agents, so it filters by user.
    /// </summary>
    void OnChanged(Guid userId, Guid agentId);
}
