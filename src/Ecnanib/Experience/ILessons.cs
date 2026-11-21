using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Experience;

/// <summary>
/// The lessons of a user: what a prompt recalls of them (FR-EXP-7), and what their user reads
/// and does with them (FR-EXP-8). See "Lessons" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// It is handed the user by id, as <see cref="ITrackRecords"/> is: Experience may not use
/// Accounts. Every question starts with the rows of that user, so a lesson of anyone else is
/// neither recalled nor found (NFR-SEC-3). See docs/ownership.md.
/// </para>
/// <para>
/// A lesson is named by the id of its position: a position has one.
/// </para>
/// </remarks>
public interface ILessons
{
    /// <summary>
    /// The lessons that a prompt of an agent recalls, as the texts that the prompt is handed, in
    /// the order it numbers them in: the lessons of the agent first, the most recently closed
    /// first, then those of the user's other agents on the same symbol, market, and timeframe.
    /// At most <c>Experience:LessonsPerPrompt</c> of them, within the size budget of
    /// <see cref="LessonRecall"/>. Only a lesson that is written and enabled is among them.
    /// </summary>
    /// <param name="userId">The user whose agent the prompt is for.</param>
    /// <param name="scope">The agent, with what it trades now.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    Task<IReadOnlyList<string>> RecallAsync(Guid userId, LessonScope scope, CancellationToken cancellationToken);

    /// <summary>One page of the lessons of a user that are written, the most recently closed first.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="agentId">The agent whose lessons are asked for, or null for those of every agent.</param>
    /// <param name="page">The page, from 1. A page after the last one has no lesson.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    Task<LessonList> OfUserAsync(Guid userId, Guid? agentId, int page, CancellationToken cancellationToken);

    /// <summary>One lesson of a user that is written, or null when that user has none of that id.</summary>
    Task<LessonView?> FindAsync(Guid userId, Guid lessonId, CancellationToken cancellationToken);

    /// <summary>Disables a lesson, or enables it again. A disabled lesson is recalled into no prompt.</summary>
    /// <returns>Whether that user has a written lesson of that id.</returns>
    Task<bool> SetEnabledAsync(Guid userId, Guid lessonId, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a lesson, for good. It is not written again: a lesson is written once for its
    /// position. A lesson that is not there is answered as one that was deleted.
    /// </summary>
    Task DeleteAsync(Guid userId, Guid lessonId, CancellationToken cancellationToken);

    /// <summary>
    /// Resets the lessons of an agent: deletes every one of them, with those that wait to be
    /// written, so that the agent starts with none. The lessons of the user's other agents stay.
    /// </summary>
    /// <returns>How many lessons that were written went.</returns>
    Task<int> ResetAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>The agent that lessons are recalled for, with what it trades now.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="Market">Its market.</param>
/// <param name="Timeframe">Its timeframe.</param>
public sealed record LessonScope(Guid AgentId, string Symbol, Market Market, Timeframe Timeframe);

/// <summary>A lesson as its user reads it.</summary>
/// <param name="Id">The id of its position, which names the lesson.</param>
/// <param name="AgentId">The agent that held the position.</param>
/// <param name="Mode">The mode that the position was in.</param>
/// <param name="Market">Its market.</param>
/// <param name="Symbol">Its symbol.</param>
/// <param name="Timeframe">Its timeframe.</param>
/// <param name="ClosedAt">When the position was closed.</param>
/// <param name="ResultInR">The result of the position in R, or null where it has none.</param>
/// <param name="Setup">The setup.</param>
/// <param name="Expected">What was expected.</param>
/// <param name="Happened">What happened.</param>
/// <param name="Takeaway">The one takeaway.</param>
/// <param name="Tags">What the lesson is about.</param>
/// <param name="Enabled">Whether it may be recalled into a prompt.</param>
/// <param name="ModelId">The model that wrote it.</param>
/// <param name="WrittenAt">When it was written.</param>
public sealed record LessonView(
    Guid Id,
    Guid AgentId,
    TradingMode Mode,
    Market Market,
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset ClosedAt,
    decimal? ResultInR,
    string Setup,
    string Expected,
    string Happened,
    string Takeaway,
    IReadOnlyList<LessonTag> Tags,
    bool Enabled,
    Guid? ModelId,
    DateTimeOffset? WrittenAt);

/// <summary>One page of the lessons of a user, with what the page leaves out.</summary>
/// <param name="Lessons">The lessons of the page.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="Pages">How many pages there are: 1 at the least.</param>
/// <param name="Written">How many lessons there are on all pages.</param>
/// <param name="Waiting">How many closed positions wait for their lesson to be written.</param>
/// <param name="Unwritten">How many closed positions have no lesson, because none could be written.</param>
public sealed record LessonList(IReadOnlyList<LessonView> Lessons, int Page, int Pages, int Written, int Waiting, int Unwritten)
{
    /// <summary>How many lessons a page has at the most.</summary>
    public const int PageSize = 25;
}

/// <summary>
/// The lessons that are to be written, for the job of the Decision Engine that has them written
/// (FR-EXP-6): only it calls a model. See "Writing a lesson" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It works across users.</b> A job has no user who is signed in: each row says whose it is,
/// and what is written of a row is written to that row alone. See "What is not asked for by
/// owner" in docs/ownership.md.
/// </para>
/// <para>
/// <b>A row is completed once.</b> An update changes a row only while it has no outcome, so a
/// row that went with its agent meanwhile fails nothing, and a row that has its outcome keeps it.
/// </para>
/// </remarks>
public interface IDueLessons
{
    /// <summary>The lessons of all users that are due and have no outcome, the longest due first.</summary>
    /// <param name="most">How many are answered at the most.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    Task<IReadOnlyList<DueLesson>> DueAsync(int most, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a lesson that is due, before its model is called: it is due again after
    /// <see cref="Lessons.ClaimedFor"/>, and not before. So a run that is stopped while the
    /// model is called leaves the lesson for a later run, and the model is not called for it
    /// again at once.
    /// </summary>
    /// <returns>Whether the lesson was taken: it was due still, and had no outcome.</returns>
    Task<bool> ClaimAsync(Guid positionId, CancellationToken cancellationToken);

    /// <summary>Gives a lesson what its model wrote, once.</summary>
    /// <param name="positionId">The position that the lesson is of.</param>
    /// <param name="words">What the lesson says.</param>
    /// <param name="modelId">The model of the catalog that wrote it.</param>
    /// <param name="templateId">The version of the lesson instructions that it was written from.</param>
    /// <param name="cancellationToken">Ends the write.</param>
    /// <returns>Whether the lesson was written: its row was there, with no outcome.</returns>
    Task<bool> WriteAsync(Guid positionId, LessonWords words, Guid modelId, int templateId, CancellationToken cancellationToken);

    /// <summary>Ends a lesson with nothing written, once, and says why.</summary>
    /// <returns>Whether the lesson was ended: its row was there, with no outcome.</returns>
    Task<bool> EndAsync(Guid positionId, LessonOutcome why, CancellationToken cancellationToken);
}

/// <summary>A lesson that is to be written.</summary>
/// <param name="PositionId">The position that it is of.</param>
/// <param name="UserId">The user whose agent held the position, and whose key its model is called with.</param>
/// <param name="AgentId">That agent, whose Risk Manager writes the lesson.</param>
public sealed record DueLesson(Guid PositionId, Guid UserId, Guid AgentId);

/// <summary>Reads, recalls, and completes the lessons that are stored.</summary>
internal sealed class Lessons(IDbContextFactory<EcnanibDbContext> contexts, ExperienceSettings settings, TimeProvider clock)
    : ILessons, IDueLessons
{
    /// <summary>How long a lesson that was taken for its model to be called is not due again.</summary>
    public static readonly TimeSpan ClaimedFor = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<string>> RecallAsync(Guid userId, LessonScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var most = settings.LessonsPerPrompt;

        if (most <= 0)
        {
            // The file says that a prompt recalls none.
            return [];
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The two reads agree with each other: a lesson that is deleted meanwhile is in neither.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by owner, first: a lesson of another user is not among them, whatever its
        // agent trades (NFR-DAT-9). And only what is written and enabled is recalled.
        var recallable = context.Set<Lesson>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(lesson => lesson.Outcome == LessonOutcome.Written && lesson.Enabled);

        // The lessons of the agent itself, whatever it traded when each was written: what an
        // agent learned stays its own, also in the other mode.
        var ofAgent = await TextsAsync(recallable.Where(lesson => lesson.AgentId == scope.AgentId), most, cancellationToken);

        // And those of the user's other agents on what this one trades now.
        var ofOthers = ofAgent.Count >= most
            ? []
            : await TextsAsync(
                recallable.Where(lesson => lesson.AgentId != scope.AgentId
                    && lesson.Symbol == scope.Symbol
                    && lesson.Market == scope.Market
                    && lesson.Timeframe == scope.Timeframe),
                most - ofAgent.Count,
                cancellationToken);

        return LessonRecall.Of(ofAgent, ofOthers, most);
    }

    public async Task<LessonList> OfUserAsync(Guid userId, Guid? agentId, int page, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The counts and the page agree with each other.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        var rows = context.Set<Lesson>().AsNoTracking().OwnedBy(userId);

        if (agentId is { } agent)
        {
            rows = rows.Where(lesson => lesson.AgentId == agent);
        }

        var written = rows.Where(lesson => lesson.Outcome == LessonOutcome.Written);

        var count = await written.CountAsync(cancellationToken);
        var waiting = await rows.CountAsync(lesson => lesson.Outcome == null, cancellationToken);
        var unwritten = await rows.CountAsync(
            lesson => lesson.Outcome != null && lesson.Outcome != LessonOutcome.Written, cancellationToken);

        // The most recently closed first, and by their ids beside it, so that the same lessons
        // are always on the same page.
        var lessons = await written
            .OrderByDescending(lesson => lesson.ClosedAt)
            .ThenByDescending(lesson => lesson.PositionId)
            .Skip((page - 1) * LessonList.PageSize)
            .Take(LessonList.PageSize)
            .ToListAsync(cancellationToken);

        return new LessonList(
            [.. lessons.Select(ViewOf)],
            page,
            Math.Max((count + LessonList.PageSize - 1) / LessonList.PageSize, 1),
            count,
            waiting,
            unwritten);
    }

    public async Task<LessonView?> FindAsync(Guid userId, Guid lessonId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // A lesson of another user is not found, like one that does not exist.
        var lesson = await context.Set<Lesson>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(row => row.PositionId == lessonId && row.Outcome == LessonOutcome.Written, cancellationToken);

        return lesson is null ? null : ViewOf(lesson);
    }

    public async Task<bool> SetEnabledAsync(Guid userId, Guid lessonId, bool enabled, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Lesson>()
            .OwnedBy(userId)
            .Where(row => row.PositionId == lessonId && row.Outcome == LessonOutcome.Written)
            .ExecuteUpdateAsync(row => row.SetProperty(lesson => lesson.Enabled, enabled), cancellationToken) > 0;
    }

    public async Task DeleteAsync(Guid userId, Guid lessonId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Only what is written is a lesson that its user reads, and so deletes. The row goes:
        // its position has its result, so nothing writes a row for it again.
        await context.Set<Lesson>()
            .OwnedBy(userId)
            .Where(row => row.PositionId == lessonId && row.Outcome == LessonOutcome.Written)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> ResetAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var ofAgent = context.Set<Lesson>().OwnedBy(userId).Where(row => row.AgentId == agentId);

        var written = await ofAgent.CountAsync(row => row.Outcome == LessonOutcome.Written, cancellationToken);

        // Every row of the agent: also one that waits, so that no lesson of a trade from before
        // the reset is written after it.
        await ofAgent.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return written;
    }

    public async Task<IReadOnlyList<DueLesson>> DueAsync(int most, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The rows of every user that are due: a job works across users. The longest due first,
        // and by their keys beside it, so that a run takes the same rows in the same order.
        return await context.Set<Lesson>()
            .AsNoTracking()
            .Where(lesson => lesson.Outcome == null && lesson.DueAt <= now)
            .OrderBy(lesson => lesson.DueAt)
            .ThenBy(lesson => lesson.PositionId)
            .Take(most)
            .Select(lesson => new DueLesson(lesson.PositionId, lesson.UserId, lesson.AgentId))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ClaimAsync(Guid positionId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Lesson>()
            .Where(row => row.PositionId == positionId && row.Outcome == null && row.DueAt <= now)
            .ExecuteUpdateAsync(row => row.SetProperty(lesson => lesson.DueAt, now + ClaimedFor), cancellationToken) > 0;
    }

    public async Task<bool> WriteAsync(
        Guid positionId, LessonWords words, Guid modelId, int templateId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(words);

        var now = clock.GetUtcNow();
        var tags = Lesson.NamesOf(words.Tags);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Lesson>()
            .Where(row => row.PositionId == positionId && row.Outcome == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(lesson => lesson.Outcome, LessonOutcome.Written)
                    .SetProperty(lesson => lesson.Setup, words.Setup)
                    .SetProperty(lesson => lesson.Expected, words.Expected)
                    .SetProperty(lesson => lesson.Happened, words.Happened)
                    .SetProperty(lesson => lesson.Takeaway, words.Takeaway)
                    .SetProperty(lesson => lesson.Tags, tags)
                    .SetProperty(lesson => lesson.ModelId, modelId)
                    .SetProperty(lesson => lesson.TemplateId, templateId)
                    .SetProperty(lesson => lesson.WrittenAt, now),
                cancellationToken) > 0;
    }

    public async Task<bool> EndAsync(Guid positionId, LessonOutcome why, CancellationToken cancellationToken)
    {
        if (why == LessonOutcome.Written)
        {
            throw new ArgumentOutOfRangeException(nameof(why), why, "A lesson that is written is given its words.");
        }

        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<Lesson>()
            .Where(row => row.PositionId == positionId && row.Outcome == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(lesson => lesson.Outcome, why)
                    .SetProperty(lesson => lesson.WrittenAt, now),
                cancellationToken) > 0;
    }

    /// <summary>The texts of some lessons as a prompt is handed them, the most recently closed first.</summary>
    private static async Task<IReadOnlyList<string>> TextsAsync(IQueryable<Lesson> lessons, int most, CancellationToken cancellationToken)
    {
        var rows = await lessons
            .OrderByDescending(lesson => lesson.ClosedAt)
            .ThenByDescending(lesson => lesson.PositionId)
            .Take(most)
            .Select(lesson => new { lesson.Setup, lesson.Expected, lesson.Happened, lesson.Takeaway })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => LessonWords.TextOf(row.Setup!, row.Expected!, row.Happened!, row.Takeaway!))];
    }

    private static LessonView ViewOf(Lesson lesson) =>
        new(
            lesson.PositionId,
            lesson.AgentId,
            lesson.Mode,
            lesson.Market,
            lesson.Symbol,
            lesson.Timeframe,
            lesson.ClosedAt,
            lesson.ResultInR,
            lesson.Setup ?? string.Empty,
            lesson.Expected ?? string.Empty,
            lesson.Happened ?? string.Empty,
            lesson.Takeaway ?? string.Empty,
            Lesson.TagsOf(lesson.Tags),
            lesson.Enabled,
            lesson.ModelId,
            lesson.WrittenAt);
}
