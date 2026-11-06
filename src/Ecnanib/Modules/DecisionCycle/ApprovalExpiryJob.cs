using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>
/// Every minute, expires the pending approvals whose window has ended, or whose agent was paused, stopped or changed
/// since, and tells their users (FR-APR-03, FR-NTF-01). Approving checks the same itself, so this only decides when
/// the status and the notice appear. Agents can't call the decision cycle, so this is how a paused agent's approval
/// ends. See Approvals in docs/architecture.md.
/// </summary>
internal sealed class ApprovalExpiryJob(
    Approvals approvals,
    IAgents agents,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    TimeProvider time,
    ILogger<ApprovalExpiryJob> logger) : IRecurringJob
{
    public const string JobName = "approval-expiry";

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromMinutes(1));

    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        List<ApprovalRow> pending;

        // At most one per agent, so this stays small.
        await using (var reading = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            pending = await reading.Set<ApprovalRow>()
                .AsNoTracking()
                .Where(row => row.Status == ApprovalStatus.Pending)
                .OrderBy(row => row.ExpiresAt)
                .ToListAsync(cancellationToken);
        }

        var expired = 0;
        var failed = 0;

        foreach (var row in pending)
        {
            // Nothing new once shutdown has begun. The next start finds the rest, because they're in the table.
            context.Stopping.ThrowIfCancellationRequested();

            // One approval's failure never stops the others (NFR-REL-05).
            try
            {
                var proposal = Approvals.ProposalOf(row);
                var agent = await agents.FindAsync(row.UserId, row.AgentId, cancellationToken);

                if (Approvals.EndFor(row, proposal, agent, time.GetUtcNow()) is { } end
                    && await approvals.ExpireAsync(row, proposal, agent, end, cancellationToken))
                {
                    expired++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogError(exception, "Expiring approval {ApprovalId} of user {UserId} failed", row.Id, row.UserId);
            }
        }

        context.Details = $"Pending {pending.Count}, expired {expired}, failed {failed}";
    }
}
