namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// Hears each change to a decision cycle's progress, once it has committed (FR-ROLE-08, FR-UI-05). The Web UI
/// subscribes for the council view and live updates (#41, #53). Register an implementation as a singleton. It's an
/// in-process event, lost on a crash, so a page reads <see cref="IDecisionTraces"/> when it opens.
/// </summary>
public interface ICycleObserver
{
    /// <summary>
    /// Called on the cycle's own thread, so it must return at once and never throw. One that throws is logged, and
    /// stops neither the cycle nor the other observers. It hears every user's cycles, so it filters by user.
    /// </summary>
    void OnChanged(CycleProgress progress);
}
