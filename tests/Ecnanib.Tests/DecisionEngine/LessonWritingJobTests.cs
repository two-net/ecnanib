using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Experience;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// After each closed position, the agent's Risk Manager model writes a short lesson: the setup,
/// what was expected, what happened, and one takeaway of at most 60 words (FR-EXP-6). A job has
/// the lessons written, every few minutes, for the positions that have their result. See
/// "Writing a lesson" in docs/experience.md, and docs/background-work.md.
/// </summary>
/// <remarks>
/// No application of the tests runs the job on a schedule: a test runs it itself, with
/// <see cref="StandInModels"/> in the place of the AI providers and <see cref="StandInKeys"/> in
/// the place of what hands a stored key over. An agent is given a cycle with its orders, as the
/// application stores them, which leaves a closed position with its result and its lesson to be
/// written.
/// </remarks>
public class LessonWritingJobTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string JobArea = "Ecnanib.DecisionEngine.LessonWritingJob";
    private const string WritingArea = "Ecnanib.DecisionEngine.LessonWriting";
    private const string RunsArea = "Ecnanib.DecisionEngine.LessonRuns";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_lessons_are_written_every_five_minutes()
    {
        using var writing = new Writing();
        var job = writing.Application.LessonWriting;

        Assert.Equal("lesson-writing", job.Name);
        Assert.Equal("every 00:05:00", job.Schedule.ToString());
        Assert.Equal(Now.AddMinutes(5), job.Schedule.NextRun(Now, Now.AddSeconds(30)));
    }

    [Fact]
    public async Task After_a_closed_position_the_model_of_the_agents_Risk_Manager_writes_its_lesson()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1), name: "Swing");

        // The Risk Manager has a model of its own, of another provider than the other roles.
        var gemini = await application.GiveRoleAsync(user.Id, agent, Role.RiskManager, Now.AddDays(-1), "gemini", "gemini-test-model");

        // A cycle that opened a position 39 seconds after its close, which its stop-loss closed
        // half an hour later, with its result: the lesson of the position waits to be written.
        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));

        Assert.Equal((0, 1), await CountsAsync(application, user.Id));

        await application.WriteLessonsAsync();

        // One call, of the model that the Risk Manager of the agent has, at its provider, with
        // the key of the agent's user for that provider, the schema of a lesson, and no search.
        var call = Assert.Single(writing.Models.LessonCalls);

        Assert.Equal(("gemini", "gemini-test-model"), (call.Provider.Value, call.Request.Model));
        Assert.Equal(StandInKeys.KeyOf(user.Id, new AiProviderCode("gemini")), call.ApiKey);
        Assert.Equal(application.RoleResults.LessonSchema, call.Request.ResultSchema);
        Assert.False(call.Request.LiveSearch);
        Assert.Equal([(user.Id, "gemini")], writing.Keys.Asked);

        // No role of a cycle is called for it.
        Assert.Empty(writing.Models.Calls);

        // The model is sent the lesson instructions, and the closed position: what it was
        // opened with, how it ended, and its result, with the plan that opened it and the
        // review of that plan as the record of its cycle keeps them.
        Assert.Equal(["Lesson instructions", "Closed position"], ComposedPrompts.HeadingsOf(call.Request.Prompt));
        Assert.Equal(
            "Symbol: BTCUSDT, priced in USDT\n" +
            "Market: Spot\n" +
            "Timeframe: 1H\n" +
            "Position: long, entry price 100000, stop-loss 99000, take-profit 102000\n" +
            "Closed: its stop-loss was executed\n" +
            "Held: 30 minutes\n" +
            "Result: -1.07 R, net -10.7 USDT, after fees of 0.5 USDT and funding of -0.1 USDT\n" +
            "Trade plan:\n" +
            $"{TeamAnswers.Plan()}\n" +
            "Review:\n" +
            TeamAnswers.Review(),
            ComposedPrompts.PartOf(call.Request.Prompt, "Closed position"));

        // Nothing of it says whose agent it is: no id, no name, and no address.
        Assert.DoesNotContain(user.Id.ToString(), call.Request.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(agent.ToString(), call.Request.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Swing", call.Request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ada@", call.Request.Prompt, StringComparison.OrdinalIgnoreCase);

        // The lesson is what the model wrote, with what the position says, the model that
        // wrote it, and when. It is enabled.
        var lesson = Assert.Single((await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation)).Lessons);

        Assert.Equal(
            (
                "Long BTCUSDT on the hour, on a breakout above the range high.",
                "Continuation to the next resistance.",
                "The price fell back into the range, and the stop-loss was executed at -1.07 R.",
                "Wait for the retest of a broken level before entering."),
            (lesson.Setup, lesson.Expected, lesson.Happened, lesson.Takeaway));
        Assert.Equal([LessonTag.Entry, LessonTag.Timing], lesson.Tags);
        Assert.Equal(
            (agent, TradingMode.Demo, Market.Spot, "BTCUSDT", Timeframe.OneHour, Now.AddHours(-1).AddSeconds(39).AddMinutes(30), -1.07m),
            (lesson.AgentId, lesson.Mode, lesson.Market, lesson.Symbol, lesson.Timeframe, lesson.ClosedAt, lesson.ResultInR));
        Assert.Equal(((Guid?)gemini, (DateTimeOffset?)Now, true), (lesson.ModelId, lesson.WrittenAt, lesson.Enabled));
        Assert.Equal((1, 0), await CountsAsync(application, user.Id));

        // And the next prompts of the agent recall it (FR-EXP-7).
        Assert.Equal(
            [
                "Setup: Long BTCUSDT on the hour, on a breakout above the range high. Expected: Continuation to the next resistance. " +
                "What happened: The price fell back into the range, and the stop-loss was executed at -1.07 R. " +
                "Takeaway: Wait for the retest of a broken level before entering.",
            ],
            await application.Lessons.RecallAsync(user.Id, new LessonScope(agent, "BTCUSDT", Market.Spot, Timeframe.OneHour), Cancellation));

        // The log says that it was written, with the agent and the symbol, and nothing of what
        // the model wrote.
        var events = application.Events();

        Assert.Equal(
            "The lesson of the position of agent {AgentId} in {Symbol} was written",
            Assert.Single(events, written => written.Area == WritingArea).Template);
        Assert.Equal(
            "{Lessons} lessons of closed positions were written, and {Unwritten} positions were left without one",
            Assert.Single(events, written => written.Area == JobArea).Template);
        Assert.DoesNotContain(events, written => written.ToString().Contains("retest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lesson_is_written_once_and_a_lesson_that_its_user_deleted_is_not_written_again()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1));

        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));
        await application.WriteLessonsAsync();

        var lesson = Assert.Single((await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation)).Lessons);

        // A run that finds nothing due calls no model, asks for no key, and says nothing.
        var asked = writing.Keys.Asked.Count;
        var said = application.Events().Count(written => written.Area == JobArea);

        await application.WriteLessonsAsync();
        writing.Clock.Set(Now.AddDays(2));
        await application.WriteLessonsAsync();

        Assert.Single(writing.Models.LessonCalls);
        Assert.Equal(asked, writing.Keys.Asked.Count);
        Assert.Equal(said, application.Events().Count(written => written.Area == JobArea));

        // Its user deletes it. The position has its result still, and nothing writes a lesson
        // for it again: a lesson is written once for its position.
        await application.Lessons.DeleteAsync(user.Id, lesson.Id, Cancellation);

        await application.RecordResultsAsync();
        await application.WriteLessonsAsync();
        writing.Clock.Set(Now.AddDays(3));
        await application.WriteLessonsAsync();

        Assert.Single(writing.Models.LessonCalls);
        Assert.Empty((await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation)).Lessons);
        Assert.Equal((0, 0), await CountsAsync(application, user.Id));
    }

    [Fact]
    public async Task Recording_the_result_of_a_closed_position_leaves_its_lesson_to_be_written()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1), market: Market.Futures, timeframe: Timeframe.FourHours);

        // A position that is closed, and one that is open: neither has a result, so neither has
        // a lesson to be written.
        var closed = await application.GiveTradeAsync(user.Id, agent, Now.AddMinutes(-10), recorded: false);
        await application.GiveTradeAsync(user.Id, agent, Now.AddMinutes(-5), closed: false);

        Assert.Empty(await application.DueLessons.DueAsync(most: 10, Cancellation));

        await application.WriteLessonsAsync();

        Assert.Empty(writing.Models.LessonCalls);

        // The job that records the results gives the closed one its result, and with it, in the
        // same step, its lesson to be written: due at once, for that position and its user.
        await application.RecordResultsAsync();

        Assert.Equal([new DueLesson(closed, user.Id, agent)], await application.DueLessons.DueAsync(most: 10, Cancellation));
        Assert.Single(application.RowsOf(user.Id)["position_results"]);
        Assert.Single(application.RowsOf(user.Id)["lessons"]);

        // Recorded once, and left once: a second run of that job finds nothing.
        await application.RecordResultsAsync();

        Assert.Single(application.RowsOf(user.Id)["lessons"]);

        await application.WriteLessonsAsync();

        // The position was opened by no cycle that is recorded, so the model is told that
        // neither a plan nor a review is.
        var call = Assert.Single(writing.Models.LessonCalls);
        var position = ComposedPrompts.PartOf(call.Request.Prompt, "Closed position");

        Assert.StartsWith("Symbol: BTCUSDT, priced in USDT\nMarket: Futures\nTimeframe: 4H\nPosition: long, entry price 100000, stop-loss 99000, take-profit 102000\n", position, StringComparison.Ordinal);
        Assert.EndsWith("\nTrade plan:\nnot recorded\nReview:\nnot recorded", position, StringComparison.Ordinal);

        var lesson = Assert.Single((await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation)).Lessons);

        Assert.Equal((closed, Market.Futures, Timeframe.FourHours, Now.AddMinutes(-10)), (lesson.Id, lesson.Market, lesson.Timeframe, lesson.ClosedAt));
    }

    [Theory]
    [InlineData("timed out")]
    [InlineData("invalid")]
    [InlineData("61 words")]
    [InlineData("an address")]
    [InlineData("no json")]
    public async Task A_call_that_times_out_or_gives_an_invalid_lesson_is_made_once_more(string first)
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1));

        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));

        // The first answer is none that a lesson is made of. The second is a lesson.
        writing.Models.SaysLesson(Failing(first), StandInModels.Answered(TeamAnswers.Lesson(takeaway: "The second answer.")));

        await application.WriteLessonsAsync();

        // Called once more, with the same prompt, and with the key asked for again for that
        // call alone (FR-KEY-9).
        Assert.Equal(2, writing.Models.LessonCalls.Count);
        Assert.Equal(writing.Models.LessonCalls[0].Request, writing.Models.LessonCalls[1].Request);
        Assert.Equal(2, writing.Keys.Asked.Count);

        var lesson = Assert.Single((await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation)).Lessons);

        Assert.Equal("The second answer.", lesson.Takeaway);

        // A rejection says the field and the rule, in the service's own words, and nothing of
        // the answer: the address that a model wrote is in no event.
        var events = application.Events();

        if (first is "61 words" or "an address" or "no json")
        {
            var rejected = Assert.Single(events, written => written.Area == RunsArea);

            Assert.Equal(
                "The lesson of a position of agent {AgentId} was rejected on attempt {Attempt}: {RejectedField} is {RejectionRule}",
                rejected.Template);
            Assert.Equal(
                first switch
                {
                    "61 words" => ("takeaway", "TextTooLong"),
                    "an address" => ("answer", "HoldsWhatAPromptNeverHolds"),
                    _ => ("answer", "NotJson"),
                },
                (rejected.Text("RejectedField"), rejected.Text("RejectionRule")));
        }

        Assert.DoesNotContain(events, written => written.ToString().Contains("grace@example.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("timed out", "timed out", 2)]
    [InlineData("61 words", "invalid", 2)]
    [InlineData("an address", "an address", 2)]
    [InlineData("failed", null, 1)]
    [InlineData("not found", null, 1)]
    public async Task A_model_that_gives_no_valid_lesson_is_not_asked_again_and_the_position_has_none(string first, string? second, int calls)
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1));

        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));

        // A call that timed out or gave an invalid lesson is made once more, and no more. A
        // call that failed, or whose model the provider does not have, is not made again at all.
        AiCallResult[] answers = second is null ? [Failing(first)] : [Failing(first), Failing(second)];

        writing.Models.SaysLesson(answers);

        await application.WriteLessonsAsync();

        Assert.Equal(calls, writing.Models.LessonCalls.Count);

        // No lesson, and the page of the lessons says that the position has none.
        var list = await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation);

        Assert.Empty(list.Lessons);
        Assert.Equal((0, 0, 1), (list.Written, list.Waiting, list.Unwritten));
        Assert.Empty(await application.Lessons.RecallAsync(user.Id, new LessonScope(agent, "BTCUSDT", Market.Spot, Timeframe.OneHour), Cancellation));

        // Every call is billed to its user: nothing calls for that position again, in the next
        // run or a day later, whatever the model would answer by then.
        await application.WriteLessonsAsync();
        writing.Clock.Set(Now.AddDays(1));
        await application.WriteLessonsAsync();

        Assert.Equal(calls, writing.Models.LessonCalls.Count);
        Assert.Empty((await application.Lessons.OfUserAsync(user.Id, agent, page: 1, Cancellation)).Lessons);

        // The log says why, as a warning, with the agent and the symbol.
        var ended = Assert.Single(application.Events(), written => written.Area == WritingArea);

        Assert.Equal("The lesson of the position of agent {AgentId} in {Symbol} was not written: {LessonOutcome}", ended.Template);
        Assert.Equal(("Warning", "NoResult", "BTCUSDT"), (ended.Level, ended.Text("LessonOutcome"), ended.Text("Symbol")));
    }

    [Theory]
    [InlineData(AiCallOutcome.ProviderPaused)]
    [InlineData(AiCallOutcome.KeyPaused)]
    public async Task A_lesson_whose_call_is_held_back_while_the_calls_are_paused_is_left_for_a_later_run(AiCallOutcome paused)
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1));

        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));

        // The gateway holds the call back: the calls to the provider, or the calls with the key,
        // are paused after failures in a row (NFR-REL-4). Nothing was sent, and nothing billed.
        writing.Models.SaysLesson(new AiCallResult(paused, null, null, TimeSpan.Zero));

        await application.WriteLessonsAsync();

        // The gateway was asked once, and not once more. The lesson has no outcome: it waits,
        // and the position is not one that has none.
        Assert.Single(writing.Models.LessonCalls);
        Assert.Equal((0, 1, 0), await AllCountsAsync(application, user.Id));

        // It is not an ending of the lesson, and no warning: that the calls are paused was said
        // when the pause began.
        Assert.DoesNotContain(application.Events(), written => written.Area == WritingArea);

        var left = Assert.Single(
            application.Events(),
            written => written.Template?.StartsWith("The lesson of a position of agent {AgentId} is left for a later run", StringComparison.Ordinal) == true);

        Assert.Equal(("Information", "claude", paused.ToString()), (left.Level, left.Text("Provider"), left.Text("AiCallOutcome")));

        // It was taken before the gateway was asked, so it is due again an hour later, as a
        // lesson is that a run left when it was stopped. By then the calls are made again.
        writing.Clock.Set(Now.AddMinutes(59));
        await application.WriteLessonsAsync();

        Assert.Single(writing.Models.LessonCalls);

        writing.Clock.Set(Now.AddHours(1));
        await application.WriteLessonsAsync();

        Assert.Equal(2, writing.Models.LessonCalls.Count);
        Assert.Equal((1, 0, 0), await AllCountsAsync(application, user.Id));
    }

    [Fact]
    public async Task A_key_that_the_provider_refuses_is_checked_again_and_the_model_is_not_called_once_more()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1));

        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));
        writing.Models.SaysLesson(StandInModels.Refused);

        await application.WriteLessonsAsync();

        // The provider is asked about the key, once, which marks it and tells what depends on
        // it (FR-KEY-3). The call is not made again with a key that was refused.
        Assert.Single(writing.Models.LessonCalls);
        Assert.Equal([(user.Id, "claude")], writing.KeyChecks.Checked);
        Assert.Equal((0, 0, 1), await AllCountsAsync(application, user.Id));
    }

    [Fact]
    public async Task A_user_without_a_key_for_the_provider_of_the_Risk_Manager_has_no_lesson_written_and_no_model_called()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1));

        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));
        writing.Keys.Outcome = (_, _) => KeyHandOverOutcome.NoKey;

        await application.WriteLessonsAsync();

        Assert.Empty(writing.Models.LessonCalls);
        Assert.Equal([(user.Id, "claude")], writing.Keys.Asked);
        Assert.Equal((0, 0, 1), await AllCountsAsync(application, user.Id));

        // It says so, as a warning, and it is not asked again once the user has a key: the
        // lesson of that position is over.
        Assert.Equal(
            "NoKey",
            Assert.Single(application.Events(), written => written.Area == WritingArea && written.Level == "Warning").Text("LessonOutcome"));

        writing.Keys.Outcome = null;
        writing.Clock.Set(Now.AddHours(2));
        await application.WriteLessonsAsync();

        Assert.Empty(writing.Models.LessonCalls);
        Assert.Single(writing.Keys.Asked);
    }

    [Fact]
    public async Task An_agent_whose_Risk_Manager_has_no_model_has_no_lesson_written_and_no_key_is_asked_for()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);

        // Every role has a model but the Risk Manager.
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1), team: false);

        foreach (var role in new[] { Role.MarketAnalyst, Role.SentimentAnalyst, Role.Strategist })
        {
            await application.GiveRoleAsync(user.Id, agent, role, Now.AddDays(-1));
        }

        await application.GiveOrdersAsync(user.Id, agent, cycleId: null, Now.AddHours(-1));

        await application.WriteLessonsAsync();

        Assert.Empty(writing.Models.LessonCalls);
        Assert.Empty(writing.Keys.Asked);
        Assert.Equal((0, 0, 1), await AllCountsAsync(application, user.Id));
        Assert.Equal(
            "NoModel",
            Assert.Single(application.Events(), written => written.Area == WritingArea && written.Level == "Warning").Text("LessonOutcome"));
    }

    [Fact]
    public async Task The_lesson_of_each_user_is_written_with_that_users_own_key_and_from_that_users_own_position()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Now.AddDays(-1), name: "Ada's");
        var graces = await application.GiveAgentAsync(grace.Id, Now.AddDays(-1), name: "Grace's", symbol: "SOLUSDT");

        // The plan of Grace's cycle says something that only her lesson may be written from.
        await application.GiveCycleAsync(ada.Id, adas, Now.AddHours(-2));
        await application.GiveCycleAsync(
            grace.Id,
            graces,
            Now.AddHours(-1),
            answers: role => role == Role.Strategist ? TeamAnswers.Plan(rationale: "Only Grace's team planned this.") : TeamAnswers.Of(role));

        writing.Models.SaysLesson(TeamAnswers.Lesson(takeaway: "The first that was due."), TeamAnswers.Lesson(takeaway: "The second that was due."));

        // One run, across users: the longest due first.
        await application.WriteLessonsAsync();

        Assert.Equal(2, writing.Models.LessonCalls.Count);

        var (forAda, forGrace) = (writing.Models.LessonCalls[0], writing.Models.LessonCalls[1]);

        // Each call with the key of the user whose agent held the position, by the row of the
        // lesson, and with nothing of the other user's position in its prompt.
        Assert.Equal(StandInKeys.KeyOf(ada.Id, new AiProviderCode("claude")), forAda.ApiKey);
        Assert.Equal(StandInKeys.KeyOf(grace.Id, new AiProviderCode("claude")), forGrace.ApiKey);
        Assert.Equal([(ada.Id, "claude"), (grace.Id, "claude")], writing.Keys.Asked);

        Assert.Contains("Symbol: BTCUSDT,", forAda.Request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Only Grace's team planned this.", forAda.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Symbol: SOLUSDT,", forGrace.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Only Grace's team planned this.", forGrace.Request.Prompt, StringComparison.Ordinal);

        // And each lesson is its own user's, and nobody else's.
        Assert.Equal(
            ["The first that was due."],
            (await application.Lessons.OfUserAsync(ada.Id, agentId: null, page: 1, Cancellation)).Lessons.Select(lesson => lesson.Takeaway));
        Assert.Equal(
            ["The second that was due."],
            (await application.Lessons.OfUserAsync(grace.Id, agentId: null, page: 1, Cancellation)).Lessons.Select(lesson => lesson.Takeaway));
    }

    [Fact]
    public async Task A_run_starts_no_lesson_late_and_what_is_left_is_written_by_the_next_run()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var first = await application.GiveAgentAsync(user.Id, Now.AddDays(-1), name: "First");
        var second = await application.GiveAgentAsync(user.Id, Now.AddDays(-1), name: "Second");

        await application.GiveCycleAsync(user.Id, first, Now.AddHours(-3));
        await application.GiveCycleAsync(user.Id, second, Now.AddHours(-2));

        // The first call takes as long as a provider is given and more: two minutes of the run
        // are over when it ends.
        writing.Keys.Outcome = (_, _) =>
        {
            writing.Clock.Set(writing.Clock.GetUtcNow().AddMinutes(2));

            return KeyHandOverOutcome.Handed;
        };

        await application.WriteLessonsAsync();

        // One lesson was started, and written. The other waits, and is not taken: it is due
        // still, for the next run.
        Assert.Single(writing.Models.LessonCalls);
        Assert.Equal((1, 1), await CountsAsync(application, user.Id));
        Assert.Single(await application.DueLessons.DueAsync(most: 10, Cancellation));

        await application.WriteLessonsAsync();

        Assert.Equal(2, writing.Models.LessonCalls.Count);
        Assert.Equal((2, 0), await CountsAsync(application, user.Id));
    }

    [Fact]
    public async Task A_run_that_is_stopped_while_the_model_is_called_leaves_the_lesson_for_an_hour_later()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Now.AddDays(-1));

        await application.GiveCycleAsync(user.Id, agent, Now.AddHours(-1));

        // The application stops as the key is handed over: the call is not made, and the run
        // ends by letting the cancellation out, as a job does.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        writing.Keys.Outcome = (_, _) =>
        {
            stopping.Cancel();

            return KeyHandOverOutcome.Handed;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application.WriteLessonsAsync(stopping.Token));

        // The lesson has no outcome: it waits. It was taken before the model was called, so it
        // is not due again at once: a run that fails as it starts does not call a model with
        // every run, on the bill of its user.
        writing.Keys.Outcome = null;

        Assert.Equal((0, 1), await CountsAsync(application, user.Id));

        await application.WriteLessonsAsync();
        writing.Clock.Set(Now.AddMinutes(59));
        await application.WriteLessonsAsync();

        Assert.Empty(writing.Models.LessonCalls);

        // An hour later it is due again, and is written.
        writing.Clock.Set(Now.AddHours(1));
        await application.WriteLessonsAsync();

        Assert.Single(writing.Models.LessonCalls);
        Assert.Equal((1, 0), await CountsAsync(application, user.Id));
    }

    [Fact]
    public async Task A_lesson_whose_agent_went_meanwhile_fails_nothing_and_the_others_are_written()
    {
        using var writing = new Writing();
        var application = writing.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var gone = await application.GiveAgentAsync(user.Id, Now.AddDays(-1), name: "Gone");
        var kept = await application.GiveAgentAsync(user.Id, Now.AddDays(-1), name: "Kept");

        await application.GiveCycleAsync(user.Id, gone, Now.AddHours(-3));
        await application.GiveCycleAsync(user.Id, kept, Now.AddHours(-2));

        // The agent is deleted as its key is handed over, while the model is about to be called:
        // its position goes with it, and the lesson of that position with the position.
        var deleted = false;

        writing.Keys.Outcome = (_, _) =>
        {
            if (!deleted)
            {
                deleted = true;
                application.DeleteAgentAsync(gone).GetAwaiter().GetResult();
            }

            return KeyHandOverOutcome.Handed;
        };

        await application.WriteLessonsAsync();

        // What the model wrote for it is written nowhere, and the lesson of the other agent is.
        Assert.Equal(2, writing.Models.LessonCalls.Count);
        Assert.Equal(
            [kept],
            (await application.Lessons.OfUserAsync(user.Id, agentId: null, page: 1, Cancellation)).Lessons.Select(lesson => lesson.AgentId));
        Assert.DoesNotContain(application.Events(), written => written.Level is "Error" or "Fatal");
    }

    /// <summary>What a call is answered with that gives no lesson, by what a test calls it.</summary>
    private static AiCallResult Failing(string how) => how switch
    {
        "timed out" => StandInModels.TimedOut,
        "invalid" => StandInModels.Invalid,
        "failed" => StandInModels.Failed,
        "not found" => StandInModels.NotFound,
        "61 words" => StandInModels.Answered(TeamAnswers.Lesson(takeaway: string.Join(' ', Enumerable.Repeat("wait", 61)))),
        "an address" => StandInModels.Answered(TeamAnswers.Lesson(takeaway: "Ask grace@example.com before the next entry.")),
        "no json" => StandInModels.Answered("The lesson is to wait for the retest."),
        _ => throw new ArgumentOutOfRangeException(nameof(how), how, "No such answer."),
    };

    /// <summary>How many lessons of a user are written, and how many wait.</summary>
    private static async Task<(int Written, int Waiting)> CountsAsync(AccountsApplication application, Guid userId)
    {
        var list = await application.Lessons.OfUserAsync(userId, agentId: null, page: 1, Cancellation);

        return (list.Written, list.Waiting);
    }

    /// <summary>The same, with how many closed positions were left without a lesson.</summary>
    private static async Task<(int Written, int Waiting, int Unwritten)> AllCountsAsync(AccountsApplication application, Guid userId)
    {
        var list = await application.Lessons.OfUserAsync(userId, agentId: null, page: 1, Cancellation);

        return (list.Written, list.Waiting, list.Unwritten);
    }

    /// <summary>
    /// The application as these tests run it: with a clock that a test sets, and with stand-ins
    /// for the AI providers, for what hands a stored key over, and for what checks a key again.
    /// </summary>
    private sealed class Writing : IDisposable
    {
        public Writing()
        {
            Clock = new TestClock(Now);
            Application = AgentsApplication.With(
                new StandInMarketData(),
                Clock,
                host: builder => builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IAiProviderGateway>(Models);
                    services.AddSingleton<IKeyHandOver>(Keys);
                    services.AddSingleton<IKeyValidation>(KeyChecks);
                }));
        }

        public TestClock Clock { get; }

        public StandInModels Models { get; } = new();

        public StandInKeys Keys { get; } = new();

        public StandInKeyChecks KeyChecks { get; } = new();

        public AccountsApplication Application { get; }

        public void Dispose() => Application.Dispose();
    }
}
