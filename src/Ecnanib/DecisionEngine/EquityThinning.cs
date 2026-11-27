using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Thins the equity of the agents over time: of a UTC day whose every row is older than
/// <see cref="AfterDays"/> days, one row is kept (NFR-DAT-5). See docs/retention.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row that is kept is the latest of its day,</b> for an agent in a mode and an asset:
/// the equity that the day ended with, which is the value that the curve holds until the next
/// row. An agent that traded in two modes on a day, or whose equity changed its asset on it,
/// keeps a row for each, since neither is ever combined with the other (FR-BIN-11).
/// </para>
/// <para>
/// <b>Only a whole day is thinned,</b> one that began more than <see cref="AfterDays"/> days
/// before the day of the run. No row is written into such a day any more, so a day that was
/// thinned stays thinned.
/// </para>
/// <para>
/// <b>A day is a batch,</b> and a day with many rows is several: see <see cref="AgedRows"/>.
/// </para>
/// <para>
/// <b>It remembers how far it came, in memory.</b> The first run of an instance goes through
/// every day from the oldest row on, and a later run through the days that have come of age
/// since. Nothing depends on what it remembers but how much a run reads: an instance that
/// starts anew goes through every day once more, and deletes what was left.
/// </para>
/// <para>
/// <b>It works across users.</b> See "What is not asked for by owner" in docs/ownership.md.
/// </para>
/// </remarks>
internal sealed class EquityThinning(IDbContextFactory<EcnanibDbContext> contexts, TimeProvider clock)
{
    /// <summary>For how many days every row of the equity of an agent is kept.</summary>
    public const int AfterDays = 90;

    // The day up to which this instance has thinned. Only the job reads and writes it, and a
    // job never runs twice at once.
    private DateTimeOffset? _thinnedBefore;

    /// <summary>Thins the days that have come of age, and says how many rows that deleted.</summary>
    /// <param name="cancellationToken">Stops the run between two batches. What was deleted by then stays deleted.</param>
    public async Task<int> ThinAsync(CancellationToken cancellationToken)
    {
        // The start of the first day that is not thinned yet.
        var before = EquityMarks.DayOf(clock.GetUtcNow()).AddDays(-AfterDays);

        var from = _thinnedBefore ?? await OldestDayAsync(cancellationToken);

        var deleted = 0;

        // No row at all leaves nothing to go through.
        for (var day = from ?? before; day < before; day = day.AddDays(1))
        {
            var (start, end) = (day, day.AddDays(1));

            // Every row of the day that a later row of the same day follows, for the same agent
            // in the same mode and asset. The latest of them has none, and stays.
            deleted += await AgedRows.DeleteAsync<EquitySnapshot>(
                contexts,
                snapshots => snapshots
                    .Where(snapshot => snapshot.At >= start
                        && snapshot.At < end
                        && snapshots.Any(later => later.AgentId == snapshot.AgentId
                            && later.Mode == snapshot.Mode
                            && later.Asset == snapshot.Asset
                            && later.At > snapshot.At
                            && later.At < end))
                    .OrderBy(snapshot => snapshot.At),
                cancellationToken);
        }

        // Only now: a run that was stopped on its way starts where this one started.
        _thinnedBefore = before;

        return deleted;
    }

    /// <summary>The start of the UTC day of the oldest row, or null when there is no row.</summary>
    private async Task<DateTimeOffset?> OldestDayAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var oldest = await context.Set<EquitySnapshot>()
            .AsNoTracking()
            .OrderBy(snapshot => snapshot.At)
            .Select(snapshot => (DateTimeOffset?)snapshot.At)
            .FirstOrDefaultAsync(cancellationToken);

        return oldest is { } at ? EquityMarks.DayOf(at) : null;
    }
}

/// <summary>
/// Thins the equity of the agents over time, every hour (NFR-DAT-5). See
/// <see cref="EquityThinning"/> and docs/retention.md.
/// </summary>
/// <remarks>
/// A day comes of age once in 24 hours, so most runs find no day to thin, and read nothing. A run
/// that is done again finds what the one before it left.
/// </remarks>
internal sealed class EquityThinningJob(EquityThinning thinning, ILogger<EquityThinningJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "equity-thinning";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var deleted = await thinning.ThinAsync(cancellationToken);

        if (deleted > 0)
        {
            logger.LogInformation(
                "{Snapshots} rows of the equity of agents over time were deleted, of days that keep one row each",
                deleted);
        }
    }
}
