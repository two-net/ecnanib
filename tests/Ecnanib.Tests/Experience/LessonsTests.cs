using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// Each Strategist and Risk Manager prompt includes up to 5 recent lessons, taken first from the
/// same agent and then from the user's other agents on the same symbol, market, and timeframe,
/// within a fixed size budget (FR-EXP-7). A user can read all of their lessons, disable or
/// delete any lesson, and reset an agent's lessons (FR-EXP-8). A disabled or deleted lesson is
/// not recalled into prompts, and lessons are recalled only from the same user's agents. See
/// "Lessons" in docs/experience.md.
/// </summary>
/// <remarks>
/// The file of these tests lets a prompt recall 4 lessons. A lesson is told from another by its
/// takeaway, which is the end of its text.
/// </remarks>
public class LessonsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_prompt_recalls_the_lessons_of_the_agent_first_and_then_those_of_the_users_other_agents_on_the_same_symbol_market_and_timeframe()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);

        // The agent, one that trades the same on the same, and three that differ in one thing each.
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Agent");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");
        var otherSymbol = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Other symbol", symbol: "SOLUSDT");
        var otherMarket = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Other market", market: Market.Futures);
        var otherTimeframe = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Other timeframe", timeframe: Timeframe.FourHours);

        // The lessons of the twin are the most recent of all, and are recalled after the agent's own.
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-9), takeaway: "Own, earlier.");
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-7), takeaway: "Own, later.");
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-8), takeaway: "Twin, earliest.");
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-2), takeaway: "Twin, earlier.");
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-1), takeaway: "Twin, later.");
        await application.GiveLessonAsync(user.Id, otherSymbol, Morning.AddHours(-3), takeaway: "Of another symbol.");
        await application.GiveLessonAsync(user.Id, otherMarket, Morning.AddHours(-3), takeaway: "Of another market.");
        await application.GiveLessonAsync(user.Id, otherTimeframe, Morning.AddHours(-3), takeaway: "Of another timeframe.");

        var recalled = await application.Lessons.RecallAsync(user.Id, ScopeOf(agent), Cancellation);

        // Up to the count of the file, which is 4 here: its own two, the most recent first, and
        // then the two most recent of the twin. None of an agent that trades something else.
        Assert.Equal(["Own, later.", "Own, earlier.", "Twin, later.", "Twin, earlier."], recalled.Select(Takeaway));

        // Each as a prompt is handed it: its four parts, on one line.
        Assert.All(recalled, lesson =>
        {
            Assert.Matches("^Setup: .+ Expected: .+ What happened: .+ Takeaway: .+$", lesson);
            Assert.DoesNotContain('\n', lesson);
        });

        // The twin recalls its own three first, and then the most recent of the agent.
        Assert.Equal(
            ["Twin, later.", "Twin, earlier.", "Twin, earliest.", "Own, later."],
            (await application.Lessons.RecallAsync(user.Id, ScopeOf(twin), Cancellation)).Select(Takeaway));

        // An agent that trades something of its own recalls its own, and nothing of the others.
        Assert.Equal(
            ["Of another symbol."],
            (await application.Lessons.RecallAsync(user.Id, ScopeOf(otherSymbol, symbol: "SOLUSDT"), Cancellation)).Select(Takeaway));
        Assert.Equal(
            ["Of another market."],
            (await application.Lessons.RecallAsync(user.Id, ScopeOf(otherMarket, market: Market.Futures), Cancellation)).Select(Takeaway));
        Assert.Equal(
            ["Of another timeframe."],
            (await application.Lessons.RecallAsync(user.Id, ScopeOf(otherTimeframe, timeframe: Timeframe.FourHours), Cancellation)).Select(Takeaway));
    }

    [Theory]
    [InlineData(5, new[] { "Own, later.", "Own, earlier.", "Twin, later.", "Twin, earlier.", "Twin, earliest." })]
    [InlineData(1, new[] { "Own, later." })]
    [InlineData(0, new string[0])]
    public async Task The_configuration_file_says_how_many_lessons_a_prompt_recalls(int perPrompt, string[] expected)
    {
        using var application = Application(perPrompt);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Agent");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");

        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-9), takeaway: "Own, earlier.");
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-7), takeaway: "Own, later.");
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-8), takeaway: "Twin, earliest.");
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-2), takeaway: "Twin, earlier.");
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-1), takeaway: "Twin, later.");

        Assert.Equal(expected, (await application.Lessons.RecallAsync(user.Id, ScopeOf(agent), Cancellation)).Select(Takeaway));
    }

    [Fact]
    public async Task What_an_agent_learned_is_recalled_whatever_mode_it_was_written_in_and_whatever_the_agent_traded_then()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Agent");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");

        // The agent is in Demo, on Spot, on the hour. It has a lesson of a trade in Live, and
        // one of when it traded on Futures on 4 hours: both are its own.
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-5), takeaway: "Own, in Live.", mode: TradingMode.Live);
        await application.GiveLessonAsync(
            user.Id, agent, Morning.AddHours(-4), takeaway: "Own, of another market.", market: Market.Futures, timeframe: Timeframe.FourHours);

        // The twin has a lesson of a trade in Live on what the agent trades, which is recalled,
        // and one of when it traded something else, which is not.
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-3), takeaway: "Twin, in Live.", mode: TradingMode.Live);
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-2), takeaway: "Twin, of another market.", market: Market.Futures);

        Assert.Equal(
            ["Own, of another market.", "Own, in Live.", "Twin, in Live."],
            (await application.Lessons.RecallAsync(user.Id, ScopeOf(agent), Cancellation)).Select(Takeaway));

        // A lesson says the mode that it was written in, so that a page tells Demo from Live (FR-BIN-11).
        var read = await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation);

        Assert.Equal(
            [("Own, of another market.", TradingMode.Demo, Market.Futures, Timeframe.FourHours), ("Own, in Live.", TradingMode.Live, Market.Spot, Timeframe.OneHour)],
            read.Lessons.Select(lesson => (lesson.Takeaway, lesson.Mode, lesson.Market, lesson.Timeframe)));
    }

    [Fact]
    public async Task A_lesson_that_is_disabled_or_deleted_or_not_written_is_not_recalled()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Agent");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");

        var kept = await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-9), takeaway: "Kept.");
        var disabled = await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-8), takeaway: "Disabled.");
        var deleted = await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-7), takeaway: "Deleted.");
        var ofTwin = await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-6), takeaway: "Of the twin.");

        // One that waits to be written, and one that could not be written: neither says anything.
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-5), outcome: null);
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-4), outcome: LessonOutcome.NoResult);

        Assert.Equal(["Deleted.", "Disabled.", "Kept.", "Of the twin."], await RecalledAsync());

        Assert.True(await application.Lessons.SetEnabledAsync(user.Id, disabled, enabled: false, Cancellation));
        await application.Lessons.DeleteAsync(user.Id, deleted, Cancellation);

        Assert.Equal(["Kept.", "Of the twin."], await RecalledAsync());

        // Neither is recalled for another agent of the user either.
        Assert.True(await application.Lessons.SetEnabledAsync(user.Id, ofTwin, enabled: false, Cancellation));
        Assert.Equal(["Kept."], await RecalledAsync());
        Assert.Equal(["Kept."], (await application.Lessons.RecallAsync(user.Id, ScopeOf(twin), Cancellation)).Select(Takeaway));

        // A lesson that is enabled again is recalled again, and one that is deleted stays gone.
        Assert.True(await application.Lessons.SetEnabledAsync(user.Id, disabled, enabled: true, Cancellation));
        Assert.Equal(["Disabled.", "Kept."], await RecalledAsync());
        Assert.Null(await application.Lessons.FindAsync(user.Id, deleted, Cancellation));
        Assert.False(await application.Lessons.SetEnabledAsync(user.Id, deleted, enabled: true, Cancellation));
        Assert.NotNull(await application.Lessons.FindAsync(user.Id, kept, Cancellation));

        async Task<IEnumerable<string>> RecalledAsync() =>
            (await application.Lessons.RecallAsync(user.Id, ScopeOf(agent), Cancellation)).Select(Takeaway);
    }

    [Fact]
    [ProvesOwnershipOf("lessons")]
    public async Task A_user_recalls_reads_and_changes_nothing_of_the_lessons_of_another_user()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Both trade the same symbol on the same market and timeframe, which is what lessons of
        // other agents are recalled by: but only of the same user's agents.
        var adas = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2), name: "Ada's");
        var graces = await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2), name: "Grace's");

        var adasLesson = await application.GiveLessonAsync(ada.Id, adas, Morning.AddHours(-3), takeaway: "Ada's.");
        var gracesLesson = await application.GiveLessonAsync(grace.Id, graces, Morning.AddHours(-2), takeaway: "Grace's.");
        await application.GiveLessonAsync(grace.Id, graces, Morning.AddHours(-1), outcome: null);

        var before = application.RowsOf(grace.Id)["lessons"];

        Assert.Equal(2, before.Count);

        // Recalled: each her own, and nothing of the other. Also for a user who names the
        // agent of the other: the lessons of that agent are not hers.
        Assert.Equal(["Ada's."], (await application.Lessons.RecallAsync(ada.Id, ScopeOf(adas), Cancellation)).Select(Takeaway));
        Assert.Equal(["Grace's."], (await application.Lessons.RecallAsync(grace.Id, ScopeOf(graces), Cancellation)).Select(Takeaway));
        Assert.Equal(["Ada's."], (await application.Lessons.RecallAsync(ada.Id, ScopeOf(graces), Cancellation)).Select(Takeaway));

        // Read: each her own, also when she names the agent of the other, and no count of hers
        // has anything of the other in it.
        var ofAda = await application.Lessons.OfUserAsync(ada.Id, agentId: null, page: 1, Cancellation);
        var ofGrace = await application.Lessons.OfUserAsync(grace.Id, agentId: null, page: 1, Cancellation);

        Assert.Equal(["Ada's."], ofAda.Lessons.Select(lesson => lesson.Takeaway));
        Assert.Equal((1, 0, 0), (ofAda.Written, ofAda.Waiting, ofAda.Unwritten));
        Assert.Equal(["Grace's."], ofGrace.Lessons.Select(lesson => lesson.Takeaway));
        Assert.Equal((1, 1, 0), (ofGrace.Written, ofGrace.Waiting, ofGrace.Unwritten));

        var ofGracesAgent = await application.Lessons.OfUserAsync(ada.Id, graces, page: 1, Cancellation);

        Assert.Empty(ofGracesAgent.Lessons);
        Assert.Equal((0, 0, 0), (ofGracesAgent.Written, ofGracesAgent.Waiting, ofGracesAgent.Unwritten));

        // A lesson of the other is not found, like one that does not exist.
        Assert.Null(await application.Lessons.FindAsync(ada.Id, gracesLesson, Cancellation));
        Assert.Null(await application.Lessons.FindAsync(ada.Id, Guid.CreateVersion7(), Cancellation));
        Assert.NotNull(await application.Lessons.FindAsync(ada.Id, adasLesson, Cancellation));

        // And nothing that Ada does changes a row of Grace: not disabling her lesson, not
        // deleting it, and not resetting the lessons of her agent.
        Assert.False(await application.Lessons.SetEnabledAsync(ada.Id, gracesLesson, enabled: false, Cancellation));
        await application.Lessons.DeleteAsync(ada.Id, gracesLesson, Cancellation);
        Assert.Equal(0, await application.Lessons.ResetAsync(ada.Id, graces, Cancellation));

        Assert.Equal(before, application.RowsOf(grace.Id)["lessons"]);
        Assert.True((await application.Lessons.FindAsync(grace.Id, gracesLesson, Cancellation))!.Enabled);

        // A user who has nothing has no lesson.
        Assert.Empty(await application.Lessons.RecallAsync(Guid.CreateVersion7(), ScopeOf(adas), Cancellation));
    }

    [Fact]
    public async Task Resetting_the_lessons_of_an_agent_deletes_every_one_of_them_and_no_lesson_of_another_agent()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Agent");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");

        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-9), takeaway: "Own, earlier.");
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-8), takeaway: "Own, later.", enabled: false);
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-7), takeaway: "Of the twin.");

        // One of the agent that waits to be written, and one that could not be: both go too, so
        // that no lesson of a trade from before the reset is written after it.
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-6), outcome: null);
        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-5), outcome: LessonOutcome.NoKey);

        // It says how many lessons that were written went.
        Assert.Equal(2, await application.Lessons.ResetAsync(user.Id, agent, Cancellation));

        var ofAgent = await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation);

        Assert.Empty(ofAgent.Lessons);
        Assert.Equal((0, 0, 0), (ofAgent.Written, ofAgent.Waiting, ofAgent.Unwritten));
        Assert.Empty(await application.DueLessons.DueAsync(most: 10, Cancellation));

        // The agent recalls none of its own, and the lesson of the twin stays, also for the agent.
        Assert.Equal(["Of the twin."], (await application.Lessons.RecallAsync(user.Id, ScopeOf(agent), Cancellation)).Select(Takeaway));
        Assert.Equal(["Of the twin."], (await application.Lessons.RecallAsync(user.Id, ScopeOf(twin), Cancellation)).Select(Takeaway));

        // Sent twice, it finds nothing the second time. The trades that the lessons were of
        // stay: a lesson is not a trade.
        Assert.Equal(0, await application.Lessons.ResetAsync(user.Id, agent, Cancellation));
        Assert.Equal(5, application.RowsOf(user.Id)["positions"].Count);
    }

    [Fact]
    public async Task The_lessons_of_a_user_are_read_a_page_at_a_time_the_most_recently_closed_first()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Agent");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");

        // 27 lessons, an hour apart, of two agents in turn. And two that are no lessons to read.
        foreach (var hour in Enumerable.Range(1, 27))
        {
            await application.GiveLessonAsync(user.Id, hour % 2 == 0 ? twin : agent, Morning.AddHours(-hour), takeaway: $"Closed {hour} hours ago.");
        }

        await application.GiveLessonAsync(user.Id, agent, Morning.AddMinutes(-30), outcome: null);
        await application.GiveLessonAsync(user.Id, agent, Morning.AddMinutes(-20), outcome: LessonOutcome.NoModel);

        var first = await application.Lessons.OfUserAsync(user.Id, agentId: null, page: 1, Cancellation);
        var second = await application.Lessons.OfUserAsync(user.Id, agentId: null, page: 2, Cancellation);

        Assert.Equal(25, LessonList.PageSize);
        Assert.Equal(Enumerable.Range(1, 25).Select(hour => $"Closed {hour} hours ago."), first.Lessons.Select(lesson => lesson.Takeaway));
        Assert.Equal(["Closed 26 hours ago.", "Closed 27 hours ago."], second.Lessons.Select(lesson => lesson.Takeaway));
        Assert.Equal((1, 2, 27, 1, 1), (first.Page, first.Pages, first.Written, first.Waiting, first.Unwritten));
        Assert.Equal((2, 2, 27, 1, 1), (second.Page, second.Pages, second.Written, second.Waiting, second.Unwritten));

        // A page after the last one has no lesson, and the counts are the same.
        var third = await application.Lessons.OfUserAsync(user.Id, agentId: null, page: 3, Cancellation);

        Assert.Empty(third.Lessons);
        Assert.Equal((2, 27), (third.Pages, third.Written));

        // Of one agent: its own, on one page, with what waits for it alone.
        var ofTwin = await application.Lessons.OfUserAsync(user.Id, twin, page: 1, Cancellation);

        Assert.Equal(Enumerable.Range(1, 13).Select(index => $"Closed {index * 2} hours ago."), ofTwin.Lessons.Select(lesson => lesson.Takeaway));
        Assert.Equal((1, 13, 0, 0), (ofTwin.Pages, ofTwin.Written, ofTwin.Waiting, ofTwin.Unwritten));

        // A lesson says what its user reads of it.
        var lesson = first.Lessons[0];

        Assert.Equal((agent, "BTCUSDT", Market.Spot, Timeframe.OneHour, TradingMode.Demo), (lesson.AgentId, lesson.Symbol, lesson.Market, lesson.Timeframe, lesson.Mode));
        Assert.Equal((Morning.AddHours(-1), 1m, true), (lesson.ClosedAt, lesson.ResultInR, lesson.Enabled));
        Assert.Equal([LessonTag.Entry, LessonTag.Timing], lesson.Tags);
        Assert.False(string.IsNullOrWhiteSpace(lesson.Setup) || string.IsNullOrWhiteSpace(lesson.Expected) || string.IsNullOrWhiteSpace(lesson.Happened));

        // A user with no lesson has one page, with nothing on it.
        var nobody = await application.Lessons.OfUserAsync(Guid.CreateVersion7(), agentId: null, page: 1, Cancellation);

        Assert.Empty(nobody.Lessons);
        Assert.Equal((1, 1, 0), (nobody.Page, nobody.Pages, nobody.Written));
    }

    [Fact]
    public async Task A_lesson_that_is_due_is_taken_before_its_model_is_called_and_is_completed_once()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock: clock);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2), name: "Ada's");
        var graces = await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2), name: "Grace's");

        // Three that wait, of two users, and one that is written: a lesson is due from a minute
        // after its position was closed here.
        var later = await application.GiveLessonAsync(ada.Id, adas, Morning.AddMinutes(-10), outcome: null);
        var earlier = await application.GiveLessonAsync(grace.Id, graces, Morning.AddMinutes(-30), outcome: null);
        var notYet = await application.GiveLessonAsync(ada.Id, adas, Morning.AddMinutes(5), outcome: null);
        await application.GiveLessonAsync(ada.Id, adas, Morning.AddHours(-2));

        // A job works across users: the lessons of both, the longest due first, each with whose
        // it is. Not the one that is not due yet, and not the one that is written.
        Assert.Equal(
            [new DueLesson(earlier, grace.Id, graces), new DueLesson(later, ada.Id, adas)],
            await application.DueLessons.DueAsync(most: 10, Cancellation));
        Assert.Equal([new DueLesson(earlier, grace.Id, graces)], await application.DueLessons.DueAsync(most: 1, Cancellation));

        // Taken once: it is due again an hour later, and not before, so a run that is cut short
        // while the model is called does not call again at once.
        Assert.True(await application.DueLessons.ClaimAsync(earlier, Cancellation));
        Assert.False(await application.DueLessons.ClaimAsync(earlier, Cancellation));
        Assert.Equal([new DueLesson(later, ada.Id, adas)], await application.DueLessons.DueAsync(most: 10, Cancellation));

        clock.Set(Morning.AddMinutes(59));
        Assert.Equal([later, notYet], (await application.DueLessons.DueAsync(most: 10, Cancellation)).Select(due => due.PositionId));

        clock.Set(Morning.AddHours(1));
        Assert.Equal([later, notYet, earlier], (await application.DueLessons.DueAsync(most: 10, Cancellation)).Select(due => due.PositionId));

        // Written once, with the model and the version of the instructions that wrote it.
        var words = LessonWords.Of("The setup.", "What was expected.", "What happened.", "The takeaway.", [LessonTag.Risk]);
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        Assert.True(await application.DueLessons.WriteAsync(earlier, words, model, templateId: 30, Cancellation));
        Assert.False(await application.DueLessons.WriteAsync(earlier, LessonWords.Of("Another.", "Another.", "Another.", "Another.", []), model, 30, Cancellation));
        Assert.False(await application.DueLessons.EndAsync(earlier, LessonOutcome.NoResult, Cancellation));

        var written = (await application.Lessons.FindAsync(grace.Id, earlier, Cancellation))!;

        Assert.Equal(
            ("The setup.", "What was expected.", "What happened.", "The takeaway.", true, (Guid?)model, (DateTimeOffset?)Morning.AddHours(1)),
            (written.Setup, written.Expected, written.Happened, written.Takeaway, written.Enabled, written.ModelId, written.WrittenAt));
        Assert.Equal([LessonTag.Risk], written.Tags);

        // Ended once, with why, and with no words: it is no lesson, and is not due again.
        Assert.True(await application.DueLessons.EndAsync(later, LessonOutcome.NoKey, Cancellation));
        Assert.False(await application.DueLessons.EndAsync(later, LessonOutcome.NoResult, Cancellation));
        Assert.False(await application.DueLessons.WriteAsync(later, words, model, 30, Cancellation));
        Assert.Null(await application.Lessons.FindAsync(ada.Id, later, Cancellation));
        Assert.Equal([notYet], (await application.DueLessons.DueAsync(most: 10, Cancellation)).Select(due => due.PositionId));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => application.DueLessons.EndAsync(notYet, LessonOutcome.Written, Cancellation));

        // A lesson that went with its agent meanwhile fails nothing.
        await application.DeleteAgentAsync(adas);

        Assert.False(await application.DueLessons.ClaimAsync(notYet, Cancellation));
        Assert.False(await application.DueLessons.WriteAsync(notYet, words, model, 30, Cancellation));
        Assert.Empty(await application.DueLessons.DueAsync(most: 10, Cancellation));
    }

    [Fact]
    public async Task The_lessons_of_an_agent_go_with_it()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Agent");
        var twin = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Twin");

        await application.GiveLessonAsync(user.Id, agent, Morning.AddHours(-3), takeaway: "Of the agent.");
        await application.GiveLessonAsync(user.Id, twin, Morning.AddHours(-2), takeaway: "Of the twin.");

        Assert.Equal(["Of the agent.", "Of the twin."], (await application.Lessons.RecallAsync(user.Id, ScopeOf(agent), Cancellation)).Select(Takeaway));

        // A lesson hangs on its position, which goes with its agent: so it is in no prompt of
        // another agent after that, and on no page.
        await application.DeleteAgentAsync(agent);

        Assert.Equal(["Of the twin."], (await application.Lessons.RecallAsync(user.Id, ScopeOf(twin), Cancellation)).Select(Takeaway));
        Assert.Equal(
            ["Of the twin."],
            (await application.Lessons.OfUserAsync(user.Id, agentId: null, page: 1, Cancellation)).Lessons.Select(lesson => lesson.Takeaway));
        Assert.Single(application.RowsOf(user.Id)["lessons"]);
    }

    private static AccountsApplication Application(int? lessonsPerPrompt = null, TestClock? clock = null) =>
        AgentsApplication.With(
            new StandInMarketData(),
            clock ?? new TestClock(Morning),
            settings: file => lessonsPerPrompt is { } most ? file.With("Experience:LessonsPerPrompt", most) : file);

    /// <summary>What lessons are recalled for: an agent, with what it trades now. Unless a test says otherwise, as an agent of the tests is given.</summary>
    private static LessonScope ScopeOf(
        Guid agentId, string symbol = "BTCUSDT", Market market = Market.Spot, Timeframe timeframe = Timeframe.OneHour) =>
        new(agentId, symbol, market, timeframe);

    /// <summary>The takeaway of a lesson as a prompt is handed it: the end of its line.</summary>
    private static string Takeaway(string lesson) => lesson[(lesson.LastIndexOf("Takeaway: ", StringComparison.Ordinal) + "Takeaway: ".Length)..];
}
