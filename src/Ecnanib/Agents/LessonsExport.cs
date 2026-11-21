using Ecnanib.Accounts;
using Ecnanib.Experience;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// The lesson of a closed position of a user's agent, as the export of the user's data holds it
/// (NFR-SEC-9, FR-EXP-8): what the position was, what the lesson says, and whether it is
/// recalled. A lesson that waits to be written, or that could not be, says so, and has no words.
/// </summary>
/// <param name="AgentId">The agent that held the position.</param>
/// <param name="Mode">The mode that the position was in.</param>
/// <param name="Market">Its market.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="Timeframe">Its timeframe, by its code.</param>
/// <param name="ClosedAt">When the position was closed.</param>
/// <param name="ResultInR">Its result in R, or null where it has none.</param>
/// <param name="Outcome">What became of the lesson: <c>Waiting</c> while it is not written, and the name of a <see cref="LessonOutcome"/> after.</param>
/// <param name="Setup">The setup, or null where nothing is written.</param>
/// <param name="Expected">What was expected.</param>
/// <param name="Happened">What happened.</param>
/// <param name="Takeaway">The one takeaway.</param>
/// <param name="Tags">What the lesson is about.</param>
/// <param name="Enabled">Whether the lesson may be recalled into a prompt.</param>
/// <param name="WrittenAt">When it was given its outcome.</param>
public sealed record ExportedLesson(
    Guid AgentId,
    TradingMode Mode,
    Market Market,
    string Symbol,
    string Timeframe,
    DateTimeOffset ClosedAt,
    decimal? ResultInR,
    string Outcome,
    string? Setup,
    string? Expected,
    string? Happened,
    string? Takeaway,
    IReadOnlyList<LessonTag> Tags,
    bool Enabled,
    DateTimeOffset? WrittenAt);

/// <summary>
/// The lessons of a user's agents, for the export of their data (NFR-SEC-9, FR-EXP-8).
/// </summary>
/// <remarks>
/// Experience keeps the lessons, and may not use Accounts, which declares what an export is made
/// of: what recalls lessons into prompts stays out of reach of personal data (FR-PRM-9). So
/// Agents hands them over, which may use both, and reads the rows of Experience as they are
/// stored. See "Exporting your data" in docs/accounts.md.
/// </remarks>
internal sealed class LessonsExport : IExportedPart
{
    /// <summary>What a lesson that is not written yet says for its outcome.</summary>
    public const string Waiting = "Waiting";

    public string Section => "lessons";

    public async Task<IReadOnlyList<object>> ReadAsync(EcnanibDbContext context, Guid userId, CancellationToken cancellationToken)
    {
        // Every row of the user, in the order that the positions were closed in: also a lesson
        // that waits, and one that could not be written, each saying which it is.
        var lessons = await context.Set<Lesson>()
            .AsNoTracking()
            .OwnedBy(userId)
            .OrderBy(lesson => lesson.ClosedAt)
            .ThenBy(lesson => lesson.PositionId)
            .ToListAsync(cancellationToken);

        return
        [
            .. lessons.Select(lesson => new ExportedLesson(
                lesson.AgentId,
                lesson.Mode,
                lesson.Market,
                lesson.Symbol,
                lesson.Timeframe.Code(),
                lesson.ClosedAt,
                lesson.ResultInR,
                lesson.Outcome?.ToString() ?? Waiting,
                lesson.Setup,
                lesson.Expected,
                lesson.Happened,
                lesson.Takeaway,
                Lesson.TagsOf(lesson.Tags),
                lesson.Enabled,
                lesson.WrittenAt)),
        ];
    }
}
