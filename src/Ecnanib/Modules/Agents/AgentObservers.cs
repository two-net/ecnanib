using Ecnanib.Modules.Agents.Contracts;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// Tells every <see cref="IAgentObserver"/> that an agent changed, after the change has committed. Resolved on first
/// use, because an observer in the Web UI may use Agents' own contracts, so injecting them would make a cycle at
/// startup.
/// </summary>
internal sealed class AgentObservers(IServiceProvider services, ILogger<AgentObservers> logger)
{
    private readonly Lazy<IReadOnlyList<IAgentObserver>> _observers = new(() => [.. services.GetServices<IAgentObserver>()]);

    /// <summary>One observer that throws can't stop the change, or the others.</summary>
    public void Changed(Guid userId, Guid agentId)
    {
        foreach (var observer in _observers.Value)
        {
            try
            {
                observer.OnChanged(userId, agentId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Telling {Observer} about agent {AgentId} failed", observer.GetType().Name, agentId);
            }
        }
    }
}
