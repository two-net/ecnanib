using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Starts the watch of every agent whose orders need it, so nothing depends on an in-process handover surviving: once at
/// startup, when protections waiting for a stop get a full window again, and then every minute (NFR-REL-02). The
/// startup pass goes through <see cref="IJobRunner"/> from <see cref="StartAsync"/>. See Order execution in
/// docs/architecture.md.
/// </summary>
internal sealed class OrderWatchSweep(OrderStore store, OrderWatch watch, IJobRunner runner, ILogger<OrderWatchSweep> logger)
    : IRecurringJob, IHostedService
{
    public const string ResumeJobName = "order-watch-resume";

    public string Name => "order-watch-sweep";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromMinutes(1));

    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(1);

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) => SweepAsync(resume: false, context, cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentKey> agents;

        try
        {
            agents = await store.ListAgentsWithWorkAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Startup goes on: the sweep finds them within a minute, without a fresh window for their stops.
            logger.LogError(exception, "Couldn't find the agents whose orders need watching at startup");
            return;
        }

        if (agents.Count > 0)
        {
            // Not awaited: startup doesn't wait for Binance. The runner records the run, and shutdown waits for it.
            _ = runner.RunAsync(ResumeJobName, Timeout, (context, token) => SweepAsync(resume: true, context, token));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SweepAsync(bool resume, JobContext context, CancellationToken cancellationToken)
    {
        var agents = await store.ListAgentsWithWorkAsync(cancellationToken);

        foreach (var agent in agents)
        {
            watch.Ensure(agent.UserId, agent.AgentId, resume);
        }

        if (agents.Count > 0)
        {
            logger.LogInformation("{Count} agents' orders need watching", agents.Count);
        }

        context.Details = $"Agents: {agents.Count}";
    }
}
