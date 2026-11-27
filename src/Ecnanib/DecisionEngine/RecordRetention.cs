using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>What a run of <see cref="RecordRetention"/> deleted.</summary>
/// <param name="Calls">The calls of roles that were kept no longer.</param>
/// <param name="Cycles">The records of cycles that were kept no longer. What hangs on a cycle went with it, and is not counted.</param>
internal sealed record RecordsRemoved(int Calls, int Cycles);

/// <summary>
/// Deletes the records of the cycles that are kept no longer (NFR-DAT-5): what the roles
/// answered after the days of <c>Retention:TranscriptDays</c>, and the record of a cycle after
/// those of <c>Retention:DecisionDays</c>. See docs/retention.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a role answered is a row of <c>role_outputs</c>,</b> and the whole row goes: the
/// answer, and how long the call took and what it used beside it. A cycle whose calls are gone
/// keeps the rest of its record until it goes itself.
/// </para>
/// <para>
/// <b>What hangs on a cycle goes with it,</b> by its keys: the calls that are left, the checks
/// of the Risk Guard with their rules, and the scored calls and the shadow trade that
/// Experience keeps. So the calls are deleted first: a setting that keeps them longer than
/// their cycles keeps none of them longer.
/// </para>
/// <para>
/// <b>Orders, executions, positions, and lessons stay.</b> None of them hangs on a cycle: an
/// order names its cycle by the id alone, and goes on naming it when the cycle is gone.
/// </para>
/// <para>
/// <b>It works across users.</b> A job has no user who is signed in, and a row is past its time
/// whoever it is of. See "What is not asked for by owner" in docs/ownership.md.
/// </para>
/// </remarks>
internal sealed class RecordRetention(
    IDbContextFactory<EcnanibDbContext> contexts,
    RetentionSettings settings,
    TimeProvider clock)
{
    /// <summary>Deletes what is kept no longer, in small batches, and says how much that was.</summary>
    /// <param name="cancellationToken">Stops the run between two batches. What was deleted by then stays deleted.</param>
    public async Task<RecordsRemoved> RemoveAgedAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // A call that is exactly as old as the calls are kept stays: it goes when it is older.
        var callsBefore = now.AddDays(-settings.TranscriptDays);
        var cyclesBefore = now.AddDays(-settings.DecisionDays);

        // A call by when it was made.
        var calls = await AgedRows.DeleteAsync<RoleOutput>(
            contexts,
            outputs => outputs
                .Where(output => output.CalledAt < callsBefore)
                .OrderBy(output => output.CalledAt),
            cancellationToken);

        // A cycle by the close of its candle, which is what every list orders the cycles by.
        var cycles = await AgedRows.DeleteAsync<DecisionCycle>(
            contexts,
            recorded => recorded
                .Where(cycle => cycle.ClosedAt < cyclesBefore)
                .OrderBy(cycle => cycle.ClosedAt),
            cancellationToken);

        return new RecordsRemoved(calls, cycles);
    }
}

/// <summary>
/// Deletes the records of the cycles that are kept no longer, every hour (NFR-DAT-5). See
/// <see cref="RecordRetention"/> and docs/retention.md.
/// </summary>
/// <remarks>
/// A run that is done again finds what the one before it left. It asks neither the exchange nor
/// a provider, and most runs delete what an hour brought past its time.
/// </remarks>
internal sealed class RecordRetentionJob(RecordRetention retention, ILogger<RecordRetentionJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "record-retention";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var removed = await retention.RemoveAgedAsync(cancellationToken);

        if (removed.Calls > 0 || removed.Cycles > 0)
        {
            logger.LogInformation(
                "{Calls} calls of roles and {Cycles} records of cycles were deleted, which were kept no longer",
                removed.Calls,
                removed.Cycles);
        }
    }
}
