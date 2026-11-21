using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Has the lessons of the closed positions written, every few minutes (FR-EXP-6): the model of
/// the agent's Risk Manager writes the lesson of a position once the position has its result.
/// See <see cref="LessonWriting"/> and "Writing a lesson" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// It runs for the lessons that are due, and for no other: a run that finds none calls no model
/// and asks for no key. A position has its result some minutes after it is closed, and its
/// lesson with the next run after that.
/// </para>
/// <para>
/// It is a job of the Decision Engine, and not of Experience, which keeps the lessons: only the
/// Decision Engine calls a model, with a prompt that it composed (NFR-SEC-8). It places no order.
/// </para>
/// </remarks>
internal sealed class LessonWritingJob(LessonWriting writing, ILogger<LessonWritingJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "lesson-writing";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var completed = await writing.WriteDueAsync(cancellationToken);

        if (completed.Written > 0 || completed.Unwritten > 0)
        {
            logger.LogInformation(
                "{Lessons} lessons of closed positions were written, and {Unwritten} positions were left without one",
                completed.Written,
                completed.Unwritten);
        }
    }
}
