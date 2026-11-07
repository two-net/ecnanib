using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Tells every <see cref="IExecutionObserver"/> that an agent's orders, position or trades changed, after the change
/// has committed. Resolved on first use, because an observer in the Web UI may use Execution's own contracts, so
/// injecting them would make a cycle at startup.
/// </summary>
internal sealed class ExecutionObservers(IServiceProvider services, ILogger<ExecutionObservers> logger)
{
    private readonly Lazy<IReadOnlyList<IExecutionObserver>> _observers = new(() => [.. services.GetServices<IExecutionObserver>()]);

    /// <summary>Once <paramref name="context"/> commits, once for each agent.</summary>
    public void AfterCommit(EcnanibDbContext context, IEnumerable<(Guid UserId, Guid AgentId)> agents)
    {
        foreach (var (userId, agentId) in agents.Distinct())
        {
            context.AfterCommit(() => Changed(userId, agentId));
        }
    }

    public void AfterCommit(EcnanibDbContext context, Guid userId, Guid agentId) => context.AfterCommit(() => Changed(userId, agentId));

    /// <summary>One observer that throws can't stop the work, or the others.</summary>
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
                logger.LogError(exception, "Telling {Observer} about agent {AgentId}'s orders failed", observer.GetType().Name, agentId);
            }
        }
    }
}
