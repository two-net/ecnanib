using System.Globalization;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.WebUi.Pages.Experience;

/// <summary>
/// The texts of the pages of the lessons, in one place (NFR-USA-4), the names of their forms,
/// and how the address of the page of the lessons is read. See "The pages of the lessons" in
/// docs/experience.md.
/// </summary>
public static class LessonPages
{
    /// <summary>The heading of the page of the lessons.</summary>
    public static string Title => "Lessons";

    /// <summary>The text of the link that leads from an experience screen to the lessons.</summary>
    public static string Link => "Lessons from closed trades";

    /// <summary>The text of the link that leads from a page of an agent to its lessons.</summary>
    public static string OfAgentLink => "Lessons of the agent";

    /// <summary>The text of the link that leads from the lessons of one agent to those of every agent.</summary>
    public static string OfEveryAgentLink => "Lessons of all your agents";

    /// <summary>The text of the link, and of the button, that resets the lessons of an agent.</summary>
    public static string Reset => "Reset the lessons";

    /// <summary>The text of the link that leaves the page that resets the lessons of an agent, with nothing reset.</summary>
    public static string KeepAll => "Keep the lessons";

    /// <summary>The text of the button that deletes a lesson.</summary>
    public static string Delete => "Delete the lesson";

    /// <summary>The text of the link that leaves the page that deletes a lesson, with nothing deleted.</summary>
    public static string Keep => "Keep the lesson";

    /// <summary>The text of the link from the lessons of an agent to its state.</summary>
    public static string StateLink => "State of the agent";

    /// <summary>The text of the link from the page of the lessons to the list of the agents.</summary>
    public static string AgentsLink => "Your agents";

    /// <summary>The text of the link from the lessons of an agent to the page that resets them: <c>Reset the lessons of Swing</c>.</summary>
    public static string ResetOf(string agentName) => $"{Reset} of {agentName}";

    /// <summary>The title and the heading of the page that resets the lessons of an agent.</summary>
    public static string ResetTitleOf(string agentName) => $"Reset the lessons of the agent {agentName}";

    /// <summary>The title and the heading of the page that deletes a lesson.</summary>
    public static string DeleteTitleOf(string agentName) => $"Delete a lesson of the agent {agentName}";

    /// <summary>What stands before the badge of the mode of the agent, under the heading of a page of the agent's lessons.</summary>
    public static string ModeLabel => "Mode:";

    /// <summary>The name of the form that resets the lessons of an agent.</summary>
    public const string ResetForm = "reset-lessons";

    /// <summary>The name of the form that deletes a lesson.</summary>
    public const string DeleteForm = "delete-lesson";

    /// <summary>What a lesson is, who writes it, and under whose key (FR-EXP-6, NFR-SEC-8).</summary>
    public static string Explained =>
        "A lesson is what one closed trade taught: the setup, what was expected, what happened, and one takeaway. " +
        "After a position of one of your agents is closed, the model of that agent's Risk Manager writes its lesson, " +
        "once, in a call under your own key for that model's provider. " +
        "It is sent the closed position, the plan that opened it, and the review of that plan, and nothing that says who you are.";

    /// <summary>What a user does with a lesson (FR-EXP-8).</summary>
    public static string ManageExplained =>
        "A lesson that you disable stays here, and is recalled into no prompt until you enable it again. " +
        "A lesson that you delete is gone for good, and is not written again. " +
        "Resetting the lessons of an agent deletes every one of them, so that the agent starts with none.";

    /// <summary>What deleting one lesson does, as the page that deletes it says.</summary>
    public static string DeleteExplained =>
        "This deletes the lesson, at once and for good. It is recalled into no prompt from then on, and it is not written " +
        "again. It cannot be undone. To keep the lesson and leave it out of the prompts, disable it instead.";

    /// <summary>What the page says of a lesson that is enabled.</summary>
    public static string EnabledExplained => "Enabled: it may be recalled into prompts.";

    /// <summary>What the page says of a lesson that is disabled.</summary>
    public static string DisabledExplained => "Disabled: it is recalled into no prompt.";

    /// <summary>What the page says when it has no lesson to show.</summary>
    public static string None => "No lesson is written yet. The first one is written after a position of one of your agents is closed.";

    /// <summary>What the page of the lessons of one agent says when that agent has none.</summary>
    public static string NoneOfAgent => "This agent has no lesson.";

    /// <summary>
    /// Where lessons are recalled, and how many (FR-EXP-7): the prompts of two roles, the lessons
    /// of the agent first, then those of the user's other agents on what it trades.
    /// </summary>
    /// <param name="perPrompt">The most lessons of a prompt, of the configuration file.</param>
    public static string RecallExplained(int perPrompt) => perPrompt <= 0
        ? "No lesson is recalled into a prompt now: the service is set to recall none."
        : $"The prompts of the Strategist and of the Risk Manager recall up to {Count(perPrompt)} of your lessons that are " +
          "enabled: the most recent lessons of the agent itself first, written in Demo or in Live, and then those of " +
          "your other agents on the same symbol, market, and timeframe. Only your own lessons are ever recalled.";

    /// <summary>The heading of the page of the lessons of one agent.</summary>
    public static string TitleOf(string agentName) => $"Lessons of the agent {agentName}";

    /// <summary>How many closed positions wait for their lesson, as a note, or null when none does.</summary>
    public static string? Waiting(int positions) => positions switch
    {
        <= 0 => null,
        1 => "The lesson of 1 closed position is not written yet. It is written within minutes of the result of the position.",
        _ => $"The lessons of {Count(positions)} closed positions are not written yet. Each is written within minutes of the result of its position.",
    };

    /// <summary>How many closed positions have no lesson because none could be written, as a note, or null when none has.</summary>
    public static string? Unwritten(int positions) => positions switch
    {
        <= 0 => null,
        1 => "1 closed position has no lesson: its model could not be called, or gave none that is valid. It is not asked again.",
        _ => $"{Count(positions)} closed positions have no lesson: their model could not be called, or gave none that is valid. " +
            "They are not asked again.",
    };

    /// <summary>Which page of the lessons a page is: <c>Page 2 of 3</c>.</summary>
    public static string PageOf(LessonList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return $"Page {Count(list.Page)} of {Count(list.Pages)}";
    }

    /// <summary>How many lessons there are, in words: <c>3 lessons</c>.</summary>
    public static string Lessons(int count) => count == 1 ? "1 lesson" : $"{Count(count)} lessons";

    /// <summary>What the page says above the lessons that it shows: how many there are, and in which order.</summary>
    public static string Listed(int count) => $"{Lessons(count)}, the most recently closed first.";

    /// <summary>What the pages of the lessons are called, for whoever does not see how they are laid out.</summary>
    public static string PagesLabel => "Pages of lessons";

    /// <summary>What leads to the page of the lessons whose positions were closed after those shown.</summary>
    public static string NewerLessons => "Newer lessons";

    /// <summary>What leads to the page of the lessons whose positions were closed before those shown.</summary>
    public static string OlderLessons => "Older lessons";

    /// <summary>What stands before the name of the agent of a lesson, which leads to the lessons of that agent.</summary>
    public static string AgentLabel => "Agent";

    /// <summary>
    /// What stands in the place of the name of the agent of a lesson where its agent is not
    /// listed: <c>Agent of this lesson</c>.
    /// </summary>
    public static string UnnamedAgent => "of this lesson";

    /// <summary>The mode of the trade of a lesson, after the name of its agent and a comma: <c>in Demo.</c></summary>
    public static string TradedIn(LessonView lesson) => $"in {ModeOf(lesson)}.";

    /// <summary>The mode of the trade of a lesson, on the page that deletes the lesson: <c>Written from a trade in Demo.</c></summary>
    public static string WrittenFrom(LessonView lesson) => $"Written from a trade in {ModeOf(lesson)}.";

    /// <summary>When the position of a lesson was closed: <c>Closed 17 November 2026, 07:20 UTC.</c></summary>
    /// <param name="closedAt">When it was closed, as the page writes a time.</param>
    public static string ClosedAt(string closedAt) => $"Closed {closedAt}.";

    /// <summary>The name of the setup of a lesson.</summary>
    public static string SetupLabel => "Setup";

    /// <summary>The name of what was expected of the trade of a lesson.</summary>
    public static string ExpectedLabel => "Expected";

    /// <summary>The name of what happened in the trade of a lesson.</summary>
    public static string HappenedLabel => "What happened";

    /// <summary>The name of the takeaway of a lesson.</summary>
    public static string TakeawayLabel => "Takeaway";

    /// <summary>The name of the tags of a lesson: what it is about.</summary>
    public static string TagsLabel => "About";

    /// <summary>The button that disables a lesson, named after the lesson.</summary>
    public static string DisableLesson(LessonView lesson, PageTimes times) => $"Disable the {NameOf(lesson, times)}";

    /// <summary>The button that enables a lesson again, named after the lesson.</summary>
    public static string EnableLesson(LessonView lesson, PageTimes times) => $"Enable the {NameOf(lesson, times)}";

    /// <summary>The link to the page that deletes a lesson, named after the lesson.</summary>
    public static string DeleteLesson(LessonView lesson, PageTimes times) => $"Delete the {NameOf(lesson, times)}";

    /// <summary>
    /// What a lesson is named by where a control says which lesson it is for: its symbol, and
    /// when its position was closed.
    /// </summary>
    public static string NameOf(LessonView lesson, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(lesson);

        return $"{lesson.Symbol} lesson of {times.At(lesson.ClosedAt)}";
    }

    /// <summary>What the heading of a lesson says where its position has no result in R.</summary>
    public static string NoResultInR => "no result in R";

    /// <summary>
    /// The heading of a lesson: what was traded, on what, and how the trade ended in R, with its
    /// sign, so that a gain is told from a loss without a colour (FR-THM-5).
    /// </summary>
    public static string HeadingOf(LessonView lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);

        var result = lesson.ResultInR is null ? NoResultInR : ExperiencePages.InR(lesson.ResultInR);

        return $"{lesson.Symbol}, {AgentPages.NameOf(lesson.Market)}, {lesson.Timeframe.Code()}: {result}";
    }

    /// <summary>The mode of a lesson as a word, so that Demo is told from Live wherever a lesson is shown (FR-BIN-11).</summary>
    public static string ModeOf(LessonView lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);

        return AgentPages.NameOf(lesson.Mode);
    }

    /// <summary>What a tag of a lesson is called on a page.</summary>
    public static string Describe(LessonTag tag) => tag switch
    {
        LessonTag.Entry => "entry",
        LessonTag.StopLoss => "stop-loss",
        LessonTag.TakeProfit => "take-profit",
        LessonTag.Risk => "risk",
        LessonTag.Trend => "trend",
        LessonTag.Range => "range",
        LessonTag.Volatility => "volatility",
        LessonTag.Sentiment => "sentiment",
        LessonTag.Funding => "funding",
        LessonTag.Fees => "fees",
        LessonTag.Timing => "timing",
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "No such tag of a lesson."),
    };

    /// <summary>The tags of a lesson, side by side.</summary>
    public static string Tags(LessonView lesson)
    {
        ArgumentNullException.ThrowIfNull(lesson);

        return string.Join(", ", lesson.Tags.Select(Describe));
    }

    /// <summary>The id of the heading of a lesson on a page, which names the lesson to a screen reader.</summary>
    public static string HeadingId(Guid lessonId) => $"lesson-{lessonId}";

    /// <summary>The name of the form that disables one lesson. Each lesson of the page has a form of its own.</summary>
    public static string DisableForm(Guid lessonId) => $"disable-lesson-{lessonId}";

    /// <summary>The name of the form that enables one lesson again.</summary>
    public static string EnableForm(Guid lessonId) => $"enable-lesson-{lessonId}";

    /// <summary>What resetting the lessons of an agent does, as the page that resets them says.</summary>
    public static string ResetExplained(int written, int waiting)
    {
        var going = written == 0
            ? "This agent has no lesson that is written."
            : $"This deletes the {Lessons(written)} of this agent, at once and for good.";
        var waits = waiting <= 0
            ? string.Empty
            : $" {(waiting == 1 ? "The lesson of 1 closed position that is not written yet is" : $"The lessons of {Count(waiting)} closed positions that are not written yet are")} not written either.";

        return $"{going}{waits} The agent recalls none of its own lessons from then on, and neither do your other agents. " +
            "The lessons of your other agents stay. It cannot be undone.";
    }

    /// <summary>
    /// What the address of the page of the lessons asks for: the lessons of one agent, or of
    /// every agent, and which page of them. Null for an address that names a choice which there
    /// is not: an agent that is no id, a page that is no whole number from 1, or either of them
    /// twice. A choice that is absent or empty is not made. What the address names besides is
    /// not gone by: the page has no other choice.
    /// </summary>
    public static (Guid? AgentId, int Page)? ChoicesOf(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var agent = query[WebUiModule.LessonsAgentKey];
        var number = query[WebUiModule.LessonsPageKey];

        // A choice that is made twice is none that there is.
        if (agent.Count > 1 || number.Count > 1)
        {
            return null;
        }

        Guid? agentId = null;
        var page = 1;

        // A choice that is absent or empty is not made.
        if (agent is [{ Length: > 0 } named])
        {
            // As the page itself writes an id, and in no other form of one.
            if (!Guid.TryParseExact(named, "D", out var id) || id.ToString() != named)
            {
                return null;
            }

            agentId = id;
        }

        if (number is [{ Length: > 0 } written]
            && (!int.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out page)
                || page < 1
                || page.ToString(CultureInfo.InvariantCulture) != written))
        {
            return null;
        }

        return (agentId, page);
    }

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
