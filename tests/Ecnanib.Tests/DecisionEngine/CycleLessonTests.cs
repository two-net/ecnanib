using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Experience;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Each Strategist and Risk Manager prompt includes up to 5 recent lessons, taken first from the
/// same agent and then from the user's other agents on the same symbol, market, and timeframe
/// (FR-EXP-7). A disabled or deleted lesson is not recalled into prompts, and lessons are
/// recalled only from the same user's agents. These run cycles as the application does, with
/// agents that are stored, and read the prompts that the models of their teams were sent. See
/// "Recalling lessons" in docs/experience.md, and "What a role is given" in docs/team.md.
/// </summary>
/// <remarks>The file of the tests lets a prompt recall 4 lessons.</remarks>
public class CycleLessonTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string RecallArea = "Ecnanib.DecisionEngine.RecalledLessons";

    /// <summary>A close of a candle of 15 minutes, which is when the cycles of these tests run.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_prompts_of_the_Strategist_and_of_the_Risk_Manager_recall_the_lessons_and_those_of_the_analysts_none()
    {
        using var cycles = new CyclesApplication(Quarter, storedAgents: true, buildsPackages: true);
        var application = cycles.Application;
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // The agent runs on 15 minutes. Its user has another agent on the same symbol, market,
        // and timeframe, and one on another timeframe. And another user has one like it.
        var agent = await application.GiveAgentAsync(
            ada.Id, Quarter.AddDays(-2), name: "Agent", state: AgentState.Running, timeframe: Timeframe.FifteenMinutes);
        var twin = await application.GiveAgentAsync(ada.Id, Quarter.AddDays(-2), name: "Twin", timeframe: Timeframe.FifteenMinutes);
        var hourly = await application.GiveAgentAsync(ada.Id, Quarter.AddDays(-2), name: "Hourly");
        var graces = await application.GiveAgentAsync(grace.Id, Quarter.AddDays(-2), name: "Grace's", timeframe: Timeframe.FifteenMinutes);

        await application.GiveLessonAsync(ada.Id, agent, Quarter.AddHours(-9), takeaway: "Own, earlier.");
        await application.GiveLessonAsync(ada.Id, agent, Quarter.AddHours(-7), takeaway: "Own, later.");
        var disabled = await application.GiveLessonAsync(ada.Id, agent, Quarter.AddHours(-6), takeaway: "Own, disabled.", enabled: false);
        var deleted = await application.GiveLessonAsync(ada.Id, agent, Quarter.AddHours(-5), takeaway: "Own, deleted.");
        await application.GiveLessonAsync(ada.Id, twin, Quarter.AddHours(-2), takeaway: "Of the twin.");
        await application.GiveLessonAsync(ada.Id, hourly, Quarter.AddHours(-1), takeaway: "Of another timeframe.");
        await application.GiveLessonAsync(grace.Id, graces, Quarter.AddHours(-1), takeaway: "Of another user.");

        await application.Lessons.DeleteAsync(ada.Id, deleted, TestContext.Current.CancellationToken);
        Assert.NotNull(await application.Lessons.FindAsync(ada.Id, disabled, TestContext.Current.CancellationToken));

        await cycles.RunAsync(Quarter).Within();

        // The cycle of the agent ran its whole team, once each.
        var models = cycles.Models;

        Assert.Equal(Enum.GetValues<Role>(), models.Roles.Order());

        // The Strategist and the Risk Manager read the same lessons, numbered in the order
        // they were recalled in: the agent's own, the most recent first, and then the one of
        // its user's other agent on the same symbol, market, and timeframe. Not the one that
        // is disabled, not the one that was deleted, not the one of another timeframe, and
        // nothing of another user.
        var recalled =
            $"1. {Lesson("Own, later.")}\n" +
            $"2. {Lesson("Own, earlier.")}\n" +
            $"3. {Lesson("Of the twin.")}";

        Assert.Equal(recalled, Lessons(models.CallsOf(Role.Strategist).Single()));
        Assert.Equal(recalled, Lessons(models.CallsOf(Role.RiskManager).Single()));

        // The two analysts are handed none.
        Assert.Equal("None.", Lessons(models.CallsOf(Role.MarketAnalyst).Single()));
        Assert.Equal("None.", Lessons(models.CallsOf(Role.SentimentAnalyst).Single()));

        // A lesson is in the part of a prompt that its instructions name as data, and in no other.
        Assert.All(models.Calls.Where(call => call.Role is Role.Strategist or Role.RiskManager), call =>
        {
            Assert.Equal(ComposedPrompts.Headings, ComposedPrompts.HeadingsOf(call.Request.Prompt));
            Assert.Equal(
                3,
                System.Text.RegularExpressions.Regex.Count(call.Request.Prompt, "Takeaway: "));
        });

        // And the instructions of both say what the recalled lessons are.
        Assert.All(new[] { Role.Strategist, Role.RiskManager }, role =>
            Assert.Contains(
                "the recalled lessons are what earlier trades taught",
                ComposedPrompts.PartOf(models.CallsOf(role).Single().Request.Prompt, "Role instructions"),
                StringComparison.Ordinal));

        // The team decided with them, as it does with none.
        Assert.Equal(CycleOutcome.Approved, cycles.Records.Of(agent).Cycle.Outcome);
    }

    [Fact]
    public async Task A_test_cycle_recalls_the_lessons_as_any_cycle_does()
    {
        using var cycles = new CyclesApplication(Quarter.AddMinutes(7), storedAgents: true, buildsPackages: true);
        var application = cycles.Application;
        var user = await application.RegisterVerifiedAsync(Ada);

        // A draft: its user runs a test cycle of it before it ever trades. It has no lesson of
        // its own, and another agent of its user on the same symbol, market, and timeframe has.
        var agent = await application.GiveAgentAsync(user.Id, Quarter.AddDays(-2), name: "Draft");
        var other = await application.GiveAgentAsync(user.Id, Quarter.AddDays(-2), name: "Other");

        await application.GiveLessonAsync(user.Id, other, Quarter.AddHours(-3), takeaway: "Of the other agent.");

        var result = await application.TestCycles
            .RunAsync(user.Id, agent, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(TestCycleOutcome.Recorded, result.Outcome);
        Assert.Equal($"1. {Lesson("Of the other agent.")}", Lessons(cycles.Models.CallsOf(Role.Strategist).Single()));
        Assert.Equal($"1. {Lesson("Of the other agent.")}", Lessons(cycles.Models.CallsOf(Role.RiskManager).Single()));
        Assert.Equal("None.", Lessons(cycles.Models.CallsOf(Role.MarketAnalyst).Single()));
    }

    [Fact]
    public async Task A_stored_lesson_that_holds_what_a_prompt_never_holds_is_left_out_and_the_cycle_decides()
    {
        using var cycles = new CyclesApplication(Quarter.AddMinutes(7), storedAgents: true, buildsPackages: true);
        var application = cycles.Application;
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Quarter.AddDays(-2));

        // A lesson is screened when it is written, so none that is stored holds an address.
        // This one is stored by the test, as if what the screen finds had changed since.
        await application.GiveLessonAsync(user.Id, agent, Quarter.AddHours(-3), takeaway: "Kept.");
        await application.GiveLessonAsync(user.Id, agent, Quarter.AddHours(-2), takeaway: "Ask ada@example.com before the next entry.");

        var result = await application.TestCycles
            .RunAsync(user.Id, agent, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // No prompt is refused for it: the cycle is recorded with a decision, and the lesson
        // that holds nothing of the kind is recalled.
        Assert.Equal(TestCycleOutcome.Recorded, result.Outcome);
        Assert.Equal(CycleOutcome.Approved, cycles.Records.Of(agent).Cycle.Outcome);
        Assert.Equal($"1. {Lesson("Kept.")}", Lessons(cycles.Models.CallsOf(Role.Strategist).Single()));
        Assert.Equal($"1. {Lesson("Kept.")}", Lessons(cycles.Models.CallsOf(Role.RiskManager).Single()));
        Assert.All(cycles.Models.Calls, call => Assert.DoesNotContain("ada@example.com", call.Request.Prompt, StringComparison.Ordinal));

        // The log says how many were left out, as a warning, and never what was found.
        var warned = Assert.Single(cycles.Written(RecallArea));

        Assert.Equal("{Lessons} lessons of agent {AgentId} are recalled into no prompt: each holds what a prompt never holds", warned.Template);
        Assert.Equal(("Warning", 1), (warned.Level, warned["Lessons"].GetInt32()));
        Assert.DoesNotContain("ada@example.com", warned.ToString(), StringComparison.Ordinal);
    }

    /// <summary>What a prompt says under "Recalled lessons".</summary>
    private static string Lessons(AskedModel call) => ComposedPrompts.PartOf(call.Request.Prompt, "Recalled lessons");

    /// <summary>A lesson of <see cref="StoredLessonRows.GiveLessonAsync"/> as a prompt is handed it, by its takeaway.</summary>
    private static string Lesson(string takeaway) =>
        LessonWords.TextOf(
            "Long on a breakout above the range high.",
            "Continuation to the next resistance.",
            "The price fell back into the range and the stop-loss was executed.",
            takeaway);
}
