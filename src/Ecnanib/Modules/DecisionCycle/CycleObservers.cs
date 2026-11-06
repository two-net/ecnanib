using Ecnanib.Modules.DecisionCycle.Contracts;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// Tells every <see cref="ICycleObserver"/> of a change, after it has committed. Resolved on first use, because an
/// observer in the Web UI may use DecisionCycle's own contracts, so injecting them would make a cycle at startup.
/// </summary>
internal sealed class CycleObservers(IServiceProvider services, ILogger<CycleObservers> logger)
{
    private readonly Lazy<IReadOnlyList<ICycleObserver>> _observers = new(() => [.. services.GetServices<ICycleObserver>()]);

    /// <summary>One observer that throws can't stop the cycle, or the others.</summary>
    public void Notify(CycleProgress progress)
    {
        foreach (var observer in _observers.Value)
        {
            try
            {
                observer.OnChanged(progress);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Telling {Observer} about decision cycle {CycleId} failed", observer.GetType().Name, progress.CycleId);
            }
        }
    }
}
