using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Starts the watch of every agent whose orders need it, every minute, so nothing depends on an in-process handover
/// surviving (NFR-REL-02). At startup, <see cref="ReconcileStartup"/> does this, after giving protections waiting for a
/// stop a full window again. See Order execution in docs/architecture.md.
/// </summary>
internal sealed class OrderWatchSweep(OrderStore store, OrderWatch watch, ILogger<OrderWatchSweep> logger) : IRecurringJob
{
    public string Name => "order-watch-sweep";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromMinutes(1));

    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(1);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var agents = await store.ListAgentsWithWorkAsync(cancellationToken);

        foreach (var agent in agents)
        {
            watch.Ensure(agent.UserId, agent.AgentId);
        }

        if (agents.Count > 0)
        {
            logger.LogInformation("{Count} agents' orders need watching", agents.Count);
        }

        context.Details = $"Agents: {agents.Count}";
    }
}
