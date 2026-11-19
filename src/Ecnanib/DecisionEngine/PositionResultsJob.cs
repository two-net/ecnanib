using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Records the results of the closed positions, every minute (FR-EXP-1): a position that is
/// closed and of which everything is stored is given its net profit or loss, its result in R, its
/// fees, its funding, and how long it was held. See <see cref="PositionResults"/> and "The
/// results of a position" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// It runs for the positions whose result is due, and for no other: most runs find none. It is
/// due as the background work starts when there is one, so a position that was closed while the
/// application did not run has its result first.
/// </para>
/// <para>
/// It asks no exchange: the executions and the funding that a result is worked out from are
/// stored by what settles the orders and by what watches the positions.
/// </para>
/// </remarks>
internal sealed class PositionResultsJob(PositionResults results, ILogger<PositionResultsJob> logger) : IBackgroundJob, IDueAtStart
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "position-results";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken) => (await results.DueAsync(cancellationToken)).Count > 0;

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var recorded = await results.RecordDueAsync(cancellationToken);

        if (recorded > 0)
        {
            logger.LogInformation("The results of {Positions} closed positions were recorded", recorded);
        }
    }
}
