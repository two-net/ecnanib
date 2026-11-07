namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// Hears that an agent changed, once the change has committed: its status, its settings, or that it was created or
/// deleted (FR-UI-05). The Web UI subscribes for live updates, and reads the agent again through
/// <see cref="IAgents"/>. Register an implementation as a singleton. It's an in-process event, lost on a crash, so a
/// page reads the agent when it opens. See Live updates in docs/architecture.md.
/// </summary>
public interface IAgentObserver
{
    /// <summary>
    /// Called on the thread that made the change, so it must return at once and never throw. One that throws is
    /// logged, and stops neither the change nor the other observers. It hears every user's agents, so it filters by
    /// user.
    /// </summary>
    void OnChanged(Guid userId, Guid agentId);
}
