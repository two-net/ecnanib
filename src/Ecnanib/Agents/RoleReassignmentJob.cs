using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.Agents;

/// <summary>
/// Gives the roles of the agents that opted in other models by strength, every hour
/// (FR-TEAM-10). See <see cref="RoleReassignments"/> and "Reassigning the roles automatically"
/// in docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// A run asks about the agents that are due, and about no other: one that opted in, runs, and
/// was not reassigned within <c>Trading:RoleReassignmentDays</c> days. An agent that holds a
/// position then is asked about again with the next run, so the hour is how long an agent that
/// is due waits at the most once it holds nothing.
/// </para>
/// <para>
/// It reads what is stored, and asks neither the exchange nor a provider. A run that is done
/// again finds the agents that it changed no longer due.
/// </para>
/// </remarks>
internal sealed class RoleReassignmentJob(RoleReassignments reassignments, ILogger<RoleReassignmentJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "role-reassignment";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var reassigned = await reassignments.ReassignDueAsync(cancellationToken);

        if (reassigned.Roles > 0)
        {
            logger.LogInformation(
                "{RoleCount} roles of {AgentCount} agents were reassigned by strength", reassigned.Roles, reassigned.Agents);
        }
    }
}
