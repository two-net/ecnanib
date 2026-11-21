using System.Globalization;
using System.Text;
using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Experience;

/// <summary>What became of the lesson of a closed position. A member is stored by its name.</summary>
public enum LessonOutcome
{
    /// <summary>The lesson is written.</summary>
    Written,

    /// <summary>The Risk Manager of the agent had no model when the lesson was due, so none was called.</summary>
    NoModel,

    /// <summary>The user of the agent had no key that could be used for the provider of that model, so none was called.</summary>
    NoKey,

    /// <summary>The prompt of the call was not composed: a part of it held what a prompt never holds (FR-PRM-9).</summary>
    NotComposed,

    /// <summary>The model was called, once more where that is done, and gave no valid lesson.</summary>
    NoResult,
}

/// <summary>
/// What a lesson is about: the fixed set that the model which writes a lesson chooses from, so a
/// tag is never text of a model's own. A member is stored by its name.
/// </summary>
public enum LessonTag
{
    Entry,
    StopLoss,
    TakeProfit,
    Risk,
    Trend,
    Range,
    Volatility,
    Sentiment,
    Funding,
    Fees,
    Timing,
}

/// <summary>
/// The limits of a lesson (FR-EXP-6). They are fixed, and are no settings: the size of what a
/// prompt recalls follows from them. See "Lessons" in docs/experience.md.
/// </summary>
public static class LessonLimits
{
    /// <summary>The most words of a takeaway, which the specification says.</summary>
    public const int TakeawayWords = 60;

    /// <summary>The most characters of a takeaway: words can be long.</summary>
    public const int TakeawayCharacters = 600;

    /// <summary>The most characters of the setup, of what was expected, and of what happened, each.</summary>
    public const int PartCharacters = 500;

    /// <summary>The most tags of a lesson.</summary>
    public const int Tags = 3;

    /// <summary>How many words a text has: what stands between its white space.</summary>
    public static int WordsIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

/// <summary>
/// What a lesson says: the setup, what was expected, what happened, and one takeaway, with what
/// it is about (FR-EXP-6). Each text is one line, and inside the limits.
/// </summary>
/// <param name="Setup">The setup of the trade.</param>
/// <param name="Expected">What was expected.</param>
/// <param name="Happened">What happened.</param>
/// <param name="Takeaway">The one takeaway, of at most <see cref="LessonLimits.TakeawayWords"/> words.</param>
/// <param name="Tags">What the lesson is about, each once, in the order of the set.</param>
public sealed record LessonWords
{
    private LessonWords(string setup, string expected, string happened, string takeaway, IReadOnlyList<LessonTag> tags)
    {
        Setup = setup;
        Expected = expected;
        Happened = happened;
        Takeaway = takeaway;
        Tags = tags;
    }

    public string Setup { get; }

    public string Expected { get; }

    public string Happened { get; }

    public string Takeaway { get; }

    public IReadOnlyList<LessonTag> Tags { get; }

    /// <summary>The lesson as a prompt is handed it: see <see cref="TextOf"/>.</summary>
    public string Text => TextOf(Setup, Expected, Happened, Takeaway);

    /// <summary>
    /// The words of a lesson, each text on one line. Whoever hands them over has validated what
    /// a model answered: words that are outside the limits are an error of the caller.
    /// </summary>
    /// <exception cref="ArgumentException">A text says nothing or is longer than it may be, or there are more tags than a lesson has.</exception>
    public static LessonWords Of(string setup, string expected, string happened, string takeaway, IEnumerable<LessonTag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var about = tags.Distinct().Order().ToList();

        if (about.Count > LessonLimits.Tags)
        {
            throw new ArgumentException($"A lesson has at most {LessonLimits.Tags} tags.", nameof(tags));
        }

        var words = new LessonWords(
            Part(setup, LessonLimits.PartCharacters, nameof(setup)),
            Part(expected, LessonLimits.PartCharacters, nameof(expected)),
            Part(happened, LessonLimits.PartCharacters, nameof(happened)),
            Part(takeaway, LessonLimits.TakeawayCharacters, nameof(takeaway)),
            about);

        if (LessonLimits.WordsIn(words.Takeaway) > LessonLimits.TakeawayWords)
        {
            throw new ArgumentException($"The takeaway of a lesson has at most {LessonLimits.TakeawayWords} words.", nameof(takeaway));
        }

        return words;

        static string Part(string text, int most, string name)
        {
            ArgumentNullException.ThrowIfNull(text, name);

            var line = OneLine(text);

            if (line.Length == 0)
            {
                throw new ArgumentException("A part of a lesson says something.", name);
            }

            return line.Length <= most
                ? line
                : throw new ArgumentException($"This part of a lesson has at most {most} characters.", name);
        }
    }

    /// <summary>
    /// A text on one line: every run of white space, line breaks, and characters that are not
    /// written is one space, and none stands at either end. So no part of a lesson begins a line
    /// of a prompt, and a lesson cannot pose as a heading of one.
    /// </summary>
    public static string OneLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var line = new StringBuilder(text.Length);
        var gap = false;

        foreach (var character in text)
        {
            if (IsGap(character))
            {
                gap = line.Length > 0;

                continue;
            }

            if (gap)
            {
                line.Append(' ');
                gap = false;
            }

            line.Append(character);
        }

        return line.ToString();

        // White space, a line or a paragraph separator, a control character, and a character
        // that formats and is not seen, such as one that changes the direction of the writing.
        static bool IsGap(char character) =>
            char.IsWhiteSpace(character)
            || char.IsControl(character)
            || char.GetUnicodeCategory(character) is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>
    /// A lesson as a prompt is handed it (FR-EXP-7): its four parts on one line, each after the
    /// name that the service gives it.
    /// </summary>
    public static string TextOf(string setup, string expected, string happened, string takeaway) =>
        $"Setup: {setup} Expected: {expected} What happened: {happened} Takeaway: {takeaway}";
}

/// <summary>
/// The lesson of a closed position, as a row of the <c>lessons</c> table: what the model of the
/// agent's Risk Manager wrote after the position was closed (FR-EXP-6), or that it waits to be
/// written, or why it was not. See "Lessons" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is written with the result of its position, and completed once.</b> What records the
/// result of a closed position writes the row with what the position says, and with no outcome.
/// A job of the Decision Engine has the model called, and gives the row its lesson, or says why
/// it has none. Nothing completes it a second time. So a lesson that its user deleted is not
/// written again, and a position whose result was recorded before the table was added has none.
/// </para>
/// <para>
/// <b>It says what its position says, in columns of its own.</b> The agent, the mode, the
/// market, the symbol, and the timeframe are those of the position, which Experience cannot
/// read: what recalls lessons for a prompt goes by them (FR-EXP-7), and so does what resets the
/// lessons of an agent (FR-EXP-8). What an agent did in Demo stays Demo (FR-BIN-11): a lesson
/// says its mode, and is recalled whatever it is.
/// </para>
/// <para>
/// <b>Only a lesson that is written and enabled is recalled.</b> Its user disables it and
/// enables it again, which changes nothing else of the row.
/// </para>
/// <para>
/// It carries its owner along to the position that it hangs on: <c>(position_id, user_id)</c>
/// points at <c>positions (id, user_id)</c>, which the Decision Engine declares, because
/// Experience may not use it. So the row goes with its position, with the agent of that
/// position, and with the account. See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="positionId">The position that the lesson is of. A position has one.</param>
/// <param name="userId">The user whose agent held the position.</param>
/// <param name="agentId">That agent.</param>
/// <param name="mode">The mode that the position was in.</param>
/// <param name="market">Its market.</param>
/// <param name="symbol">Its symbol.</param>
/// <param name="timeframe">The timeframe of the agent when the position was opened.</param>
/// <param name="closedAt">When the position was closed.</param>
/// <param name="resultInR">The result of the position in R, or null where it has none.</param>
/// <param name="dueAt">When the lesson is next to be written, while it has no outcome.</param>
/// <param name="outcome">What became of the lesson, or null while it waits.</param>
/// <param name="setup">The setup, or null while nothing is written.</param>
/// <param name="expected">What was expected.</param>
/// <param name="happened">What happened.</param>
/// <param name="takeaway">The one takeaway.</param>
/// <param name="tags">What the lesson is about, as the names of members of <see cref="LessonTag"/>.</param>
/// <param name="modelId">The model of the catalog that wrote the lesson.</param>
/// <param name="templateId">The version of the lesson instructions that it was written from (FR-PRM-6).</param>
/// <param name="writtenAt">When it was given its outcome.</param>
/// <param name="enabled">Whether the lesson may be recalled into a prompt.</param>
public sealed class Lesson(
    Guid positionId,
    Guid userId,
    Guid agentId,
    TradingMode mode,
    Market market,
    string symbol,
    Timeframe timeframe,
    DateTimeOffset closedAt,
    decimal? resultInR,
    DateTimeOffset dueAt,
    LessonOutcome? outcome = null,
    string? setup = null,
    string? expected = null,
    string? happened = null,
    string? takeaway = null,
    string[]? tags = null,
    Guid? modelId = null,
    int? templateId = null,
    DateTimeOffset? writtenAt = null,
    bool enabled = true) : IUserOwned
{
    public Guid PositionId { get; } = positionId;

    public Guid UserId { get; } = userId;

    public Guid AgentId { get; } = agentId;

    public TradingMode Mode { get; } = mode;

    public Market Market { get; } = market;

    public string Symbol { get; } = symbol;

    public Timeframe Timeframe { get; } = timeframe;

    public DateTimeOffset ClosedAt { get; } = closedAt;

    public decimal? ResultInR { get; } = resultInR;

    public DateTimeOffset DueAt { get; } = dueAt;

    public LessonOutcome? Outcome { get; } = outcome;

    public string? Setup { get; } = setup;

    public string? Expected { get; } = expected;

    public string? Happened { get; } = happened;

    public string? Takeaway { get; } = takeaway;

    public string[]? Tags { get; } = tags;

    public Guid? ModelId { get; } = modelId;

    public int? TemplateId { get; } = templateId;

    public DateTimeOffset? WrittenAt { get; } = writtenAt;

    public bool Enabled { get; } = enabled;

    /// <summary>
    /// The lesson of a closed position as it is written with the result of the position: with
    /// what the position says, due at once, and with nothing of a lesson yet.
    /// </summary>
    /// <param name="positionId">The position.</param>
    /// <param name="userId">The user whose agent held it.</param>
    /// <param name="agentId">That agent.</param>
    /// <param name="mode">The mode of the position.</param>
    /// <param name="market">Its market.</param>
    /// <param name="symbol">Its symbol.</param>
    /// <param name="timeframe">Its timeframe.</param>
    /// <param name="closedAt">When it was closed.</param>
    /// <param name="resultInR">Its result in R, or null where it has none.</param>
    /// <param name="now">When its result is recorded, which is from when the lesson is due.</param>
    public static Lesson Due(
        Guid positionId,
        Guid userId,
        Guid agentId,
        TradingMode mode,
        Market market,
        string symbol,
        Timeframe timeframe,
        DateTimeOffset closedAt,
        decimal? resultInR,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        return new Lesson(
            positionId, userId, agentId, mode, market, symbol, timeframe, closedAt.ToUniversalTime(), resultInR, now.ToUniversalTime());
    }

    /// <summary>The names that the tags of a lesson are stored under, in the order of the set.</summary>
    public static string[] NamesOf(IEnumerable<LessonTag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return [.. tags.Distinct().Order().Select(tag => tag.ToString())];
    }

    /// <summary>The tags that stored names say. A name that is none of the set says nothing.</summary>
    public static IReadOnlyList<LessonTag> TagsOf(IEnumerable<string>? names) =>
    [
        .. (names ?? [])
            .Select(name => Enum.TryParse<LessonTag>(name, out var tag) && Enum.IsDefined(tag) ? tag : (LessonTag?)null)
            .OfType<LessonTag>()
            .Distinct()
            .Order(),
    ];
}

internal sealed class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        builder.ToTable("lessons");

        // A position has one lesson: the row is found by the position, and is completed once.
        builder.HasKey(lesson => lesson.PositionId);

        builder.Property(lesson => lesson.PositionId).ValueGeneratedNever();
        builder.Property(lesson => lesson.UserId);
        builder.Property(lesson => lesson.AgentId);
        builder.Property(lesson => lesson.Mode);
        builder.Property(lesson => lesson.Market);
        builder.Property(lesson => lesson.Symbol);
        builder.Property(lesson => lesson.Timeframe);
        builder.Property(lesson => lesson.ClosedAt);
        builder.Property(lesson => lesson.ResultInR);
        builder.Property(lesson => lesson.DueAt);
        builder.Property(lesson => lesson.Outcome);
        builder.Property(lesson => lesson.Setup);
        builder.Property(lesson => lesson.Expected);
        builder.Property(lesson => lesson.Happened);
        builder.Property(lesson => lesson.Takeaway);
        builder.Property(lesson => lesson.Tags);
        builder.Property(lesson => lesson.ModelId);
        builder.Property(lesson => lesson.TemplateId);
        builder.Property(lesson => lesson.WrittenAt);
        builder.Property(lesson => lesson.Enabled);

        // The lessons of one user, by when their positions were closed: what a user reads, and
        // what is recalled from the user's other agents (FR-EXP-7, FR-EXP-8).
        builder.HasIndex(lesson => new { lesson.UserId, lesson.ClosedAt });

        // The lessons of one agent, the most recent first: what is recalled first (FR-EXP-7),
        // and what resetting the lessons of an agent deletes (FR-EXP-8).
        builder.HasIndex(lesson => new { lesson.UserId, lesson.AgentId, lesson.ClosedAt });

        // The lessons that are still to be written, which the job asks for with every run: few
        // of all the lessons that there ever were.
        builder.HasIndex(lesson => lesson.DueAt).HasFilter("outcome IS NULL");

        // A model of the catalog is never deleted (FR-CAT-5), so the row finds the model that
        // wrote the lesson for good.
        builder.HasOne<AiModel>()
            .WithMany()
            .HasForeignKey(lesson => lesson.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        // The key to the position, which carries the owner along, and the key to the version of
        // the lesson instructions are declared by the Decision Engine, in LessonKeys.cs:
        // Experience may name neither a position nor a prompt template.
    }
}
