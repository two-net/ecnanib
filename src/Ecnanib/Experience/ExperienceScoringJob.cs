using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.Experience;

/// <summary>
/// Scores the calls of the analysts and follows the shadow trades of the vetoed plans, every few
/// minutes (FR-EXP-3, FR-EXP-5): a call is given its outcome once the 4th candle after it has
/// closed, and a shadow trade its result once a later candle has ended it. See
/// <see cref="ExperienceScoring"/> and "The job" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// It runs for the rows that are due, and for no other: a run that finds none asks the exchange
/// nothing. A candle closes every quarter hour at the soonest, so nothing is due more often.
/// </para>
/// <para>
/// It reads candles, which need no key, and places no order: nothing of Experience can.
/// </para>
/// </remarks>
internal sealed class ExperienceScoringJob(ExperienceScoring scoring, ILogger<ExperienceScoringJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "experience-scoring";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var completed = await scoring.ScoreDueAsync(cancellationToken);

        if (completed.Calls > 0 || completed.ShadowTrades > 0)
        {
            logger.LogInformation(
                "{Calls} calls of analysts were scored, and {ShadowTrades} shadow trades ended",
                completed.Calls,
                completed.ShadowTrades);
        }
    }
}
