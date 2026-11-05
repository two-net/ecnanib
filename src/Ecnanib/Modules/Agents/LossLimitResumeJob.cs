using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// Every minute, resumes the agents whose daily or weekly loss limit's pause has ended (FR-RISK-08), one at a
/// time, through the same activation checks a user's resume goes through. An agent that fails them stays paused
/// for its user. The pause's end is in <c>agents.loss_limit_until</c>, so a restart loses nothing. See Agents in
/// docs/architecture.md.
/// </summary>
internal sealed class LossLimitResumeJob(
    Agents agents,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    TimeProvider time,
    ILogger<LossLimitResumeJob> logger)
    : IRecurringJob
{
    public const string JobName = "agent-loss-limit-resume";

    private static readonly AgentStatusReason[] Resumable = [AgentStatusReason.DailyLossLimit, AgentStatusReason.WeeklyLossLimit];

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromMinutes(1));

    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        List<(Guid Id, Guid UserId, AgentStatusReason Reason)> due;

        await using (var reading = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            due = [.. (await reading.Set<Agent>()
                    .AsNoTracking()
                    .Where(agent => agent.Status == AgentStatus.Paused && Resumable.Contains(agent.StatusReason)
                        && agent.LossLimitUntil != null && agent.LossLimitUntil <= now)
                    .OrderBy(agent => agent.LossLimitUntil)
                    .Select(agent => new { agent.Id, agent.UserId, agent.StatusReason })
                    .ToListAsync(cancellationToken))
                .Select(agent => (agent.Id, agent.UserId, agent.StatusReason))];
        }

        var resumed = 0;
        var held = 0;
        var failed = 0;

        foreach (var (id, userId, reason) in due)
        {
            // Nothing new once shutdown has begun. The next start finds the rest, because they're in the table.
            context.Stopping.ThrowIfCancellationRequested();

            // One agent's failure never stops the others (NFR-REL-05).
            try
            {
                if (await agents.ResumeAfterLossLimitAsync(userId, id, reason, cancellationToken) == ActivationOutcome.Activated)
                {
                    resumed++;
                }
                else
                {
                    held++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogError(exception, "Resuming agent {AgentId} of user {UserId} after its loss limit failed", id, userId);
            }
        }

        context.Details = $"Due {due.Count}, resumed {resumed}, held {held}, failed {failed}";
    }
}
