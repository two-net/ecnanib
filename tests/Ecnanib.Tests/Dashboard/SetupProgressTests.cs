using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Dashboard;

namespace Ecnanib.Tests.Dashboard;

/// <summary>
/// Where a user stands on the setup checklist of a first-time user (FR-UI-4), with no database:
/// what makes each step done, which decisions there are to review, and when the checklist is
/// shown no more. See "The setup checklist" in docs/dashboard.md.
/// </summary>
public class SetupProgressTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static readonly SetupMarks Unmarked = new(null, null);

    [Fact]
    public void A_new_account_has_done_none_of_the_four_steps_and_is_offered_no_button()
    {
        var progress = SetupProgress.Of(Keys(), [], [], [], Unmarked);

        Assert.Equal([SetupStep.Keys, SetupStep.Agent, SetupStep.Decisions, SetupStep.Live], SetupProgress.Steps);
        Assert.All(SetupProgress.Steps, step => Assert.False(progress.IsDone(step)));
        Assert.Equal(0, progress.Done);
        Assert.Null(progress.Decided);
        Assert.False(progress.CanMarkReviewed);
        Assert.False(progress.CanMarkConsidered);
        Assert.False(progress.Finished);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void The_keys_are_added_with_a_key_of_an_AI_provider_and_a_Binance_key_for_Demo(bool ai, bool demo, bool live, bool done)
    {
        var progress = SetupProgress.Of(Keys(ai, demo, live), [], [], [], Unmarked);

        Assert.Equal(ai, progress.AiKeyStored);
        Assert.Equal(demo, progress.DemoKeyStored);
        Assert.Equal(done, progress.IsDone(SetupStep.Keys));
    }

    [Fact]
    public void An_agent_is_created_once_the_user_has_one_whatever_its_mode_is_now()
    {
        Assert.False(SetupProgress.Of(Keys(), [], [], [], Unmarked).IsDone(SetupStep.Agent));
        Assert.True(SetupProgress.Of(Keys(), [Agent("Swing")], [], [], Unmarked).IsDone(SetupStep.Agent));

        // Every agent is created in Demo (FR-AGT-7): one that was switched since was created too.
        Assert.True(SetupProgress.Of(Keys(), [Agent("Swing", TradingMode.Live)], [], [], Unmarked).IsDone(SetupStep.Agent));
    }

    [Fact]
    public void The_decisions_to_review_are_those_of_Demo_first_and_of_Live_for_a_user_who_has_none_in_Demo()
    {
        var swing = Agent("Swing");
        var trend = Agent("Trend", TradingMode.Live);

        // The latest in Demo, though one in Live is later.
        var both = SetupProgress.Of(
            Keys(), [swing, trend], [Decision(swing, Morning.AddHours(-2))], [Decision(trend, Morning)], Unmarked);

        Assert.Same(swing, both.Decided);
        Assert.True(both.CanMarkReviewed);

        // A user who went to Live with no decision in Demo reviews what was decided there.
        var liveAlone = SetupProgress.Of(Keys(), [swing, trend], [], [Decision(trend, Morning)], Unmarked);

        Assert.Same(trend, liveAlone.Decided);
        Assert.True(liveAlone.CanMarkReviewed);
    }

    [Fact]
    public void A_test_cycle_and_a_cycle_of_an_agent_that_is_gone_are_no_decision_to_review()
    {
        var swing = Agent("Swing");
        var gone = Agent("Gone");

        // A cycle that its user ran as a test is not among the decisions (FR-CYC-12).
        var tested = SetupProgress.Of(Keys(), [swing], [Decision(swing, Morning, CycleKind.Test)], [], Unmarked);

        Assert.Null(tested.Decided);
        Assert.False(tested.CanMarkReviewed);

        // An agent that was deleted between the two reads of the page has nothing to lead to.
        var deleted = SetupProgress.Of(Keys(), [swing], [Decision(gone, Morning), Decision(swing, Morning.AddHours(-1))], [], Unmarked);

        Assert.Same(swing, deleted.Decided);
        Assert.Null(SetupProgress.Of(Keys(), [swing], [Decision(gone, Morning)], [], Unmarked).Decided);
    }

    [Fact]
    public void The_decisions_are_reviewed_when_the_user_said_so_and_Live_is_offered_after_that()
    {
        var swing = Agent("Swing");
        var decisions = new[] { Decision(swing, Morning) };

        // A decision alone does not do the step: its user says that they reviewed it.
        var decided = SetupProgress.Of(Keys(), [swing], decisions, [], Unmarked);

        Assert.False(decided.IsDone(SetupStep.Decisions));
        Assert.True(decided.CanMarkReviewed);
        Assert.False(decided.CanMarkConsidered);

        // Then Live, as the checklist says: it is offered once the decisions are reviewed.
        var reviewed = SetupProgress.Of(Keys(), [swing], decisions, [], new SetupMarks(Morning, null));

        Assert.True(reviewed.IsDone(SetupStep.Decisions));
        Assert.False(reviewed.CanMarkReviewed);
        Assert.False(reviewed.IsDone(SetupStep.Live));
        Assert.True(reviewed.CanMarkConsidered);
        Assert.False(reviewed.Finished);
    }

    [Fact]
    public void A_user_who_switched_to_Live_before_the_review_has_considered_Live_and_is_still_shown_the_checklist()
    {
        var trend = Agent("Trend", TradingMode.Live);
        var progress = SetupProgress.Of(Keys(), [trend], [], [Decision(trend, Morning)], new SetupMarks(null, Morning));

        Assert.True(progress.IsDone(SetupStep.Live));
        Assert.False(progress.CanMarkConsidered);
        Assert.False(progress.IsDone(SetupStep.Decisions));
        Assert.True(progress.CanMarkReviewed);
        Assert.False(progress.Finished);
    }

    [Fact]
    public void The_checklist_is_finished_once_both_are_marked_whatever_was_deleted_since()
    {
        // All four are done.
        var swing = Agent("Swing");
        var all = SetupProgress.Of(Keys(ai: true, demo: true), [swing], [Decision(swing, Morning)], [], new SetupMarks(Morning, Morning));

        Assert.Equal(4, all.Done);
        Assert.True(all.Finished);

        // The keys and the agent are deleted since: the first two steps say the account as it
        // is, and the checklist is finished all the same.
        var emptied = SetupProgress.Of(Keys(), [], [], [], new SetupMarks(Morning, Morning));

        Assert.Equal(2, emptied.Done);
        Assert.True(emptied.Finished);
    }

    [Fact]
    public void Each_step_says_what_is_done_of_it_or_what_there_is_to_do()
    {
        var swing = Agent("Swing");
        var empty = SetupProgress.Of(Keys(), [], [], [], Unmarked);

        Assert.Equal(
            "An agent needs a key of an AI provider for its team, and a Binance key for Demo to trade with. Neither is stored yet.",
            SetupPages.Says(SetupStep.Keys, empty));
        Assert.Equal(
            "An agent trades one symbol on one timeframe. It starts in Demo: Binance's test environment, with no real funds.",
            SetupPages.Says(SetupStep.Agent, empty));
        Assert.Equal(
            "No team of yours has decided anything yet. A team decides each time a candle of its agent closes, once the agent is started.",
            SetupPages.Says(SetupStep.Decisions, empty));
        Assert.Equal("This step comes after you have reviewed decisions.", SetupPages.Says(SetupStep.Live, empty));

        // Which of the two keys is missing.
        Assert.Equal(
            "A key of an AI provider is stored. Still to add: a Binance key for Demo, which your agent trades with.",
            SetupPages.Says(SetupStep.Keys, SetupProgress.Of(Keys(ai: true), [], [], [], Unmarked)));
        Assert.Equal(
            "A Binance key for Demo is stored. Still to add: a key of an AI provider, for the team of your agent.",
            SetupPages.Says(SetupStep.Keys, SetupProgress.Of(Keys(demo: true), [], [], [], Unmarked)));

        // The agent whose team decided is named.
        var decided = SetupProgress.Of(Keys(ai: true, demo: true), [swing], [Decision(swing, Morning)], [], Unmarked);

        Assert.Equal("A key of an AI provider and a Binance key for Demo are stored.", SetupPages.Says(SetupStep.Keys, decided));
        Assert.Equal("You have an agent. Every agent starts in Demo, where no money is at stake.", SetupPages.Says(SetupStep.Agent, decided));
        Assert.StartsWith("The team of Swing has decided.", SetupPages.Says(SetupStep.Decisions, decided), StringComparison.Ordinal);

        // What Live takes is said once it is offered, and that staying in Demo is a choice too.
        var reviewed = decided with { Marks = new SetupMarks(Morning, null) };

        Assert.Equal("You marked the decisions as reviewed.", SetupPages.Says(SetupStep.Decisions, reviewed));
        Assert.Contains("a valid Binance key for Live, your confirmation of the risk, and a fresh proof of who you are", SetupPages.Says(SetupStep.Live, reviewed));
        Assert.Contains("Staying in Demo is a choice too", SetupPages.Says(SetupStep.Live, reviewed));
        Assert.Equal("You considered Live.", SetupPages.Says(SetupStep.Live, reviewed with { Marks = new SetupMarks(Morning, Morning) }));

        // How many are done, as a sentence.
        Assert.Equal("None of the 4 steps is done yet.", SetupPages.Counted(0));
        Assert.Equal("1 of the 4 steps is done.", SetupPages.Counted(1));
        Assert.Equal("3 of the 4 steps are done.", SetupPages.Counted(3));
    }

    /// <summary>The keys of a user as the page of the keys reads them: every provider and both environments, each with a key or with none.</summary>
    private static StoredKeysState Keys(bool ai = false, bool demo = false, bool live = false)
    {
        var stored = new StoredKey("abcd", Morning, KeyStatus.Valid, Morning);

        return new StoredKeysState(
            AddressVerified: true,
            AiKeys:
            [
                new AiKeySlot(new AiProviderCode("claude"), "Claude", null),
                new AiKeySlot(new AiProviderCode("gemini"), "Gemini", ai ? stored : null),
            ],
            ExchangeKeys:
            [
                new ExchangeKeySlot(TradingMode.Demo, demo ? stored : null),
                new ExchangeKeySlot(TradingMode.Live, live ? stored : null),
            ]);
    }

    /// <summary>An agent of the user, as the list of the agents reads it.</summary>
    private static AgentView Agent(string name, TradingMode mode = TradingMode.Demo) => new(
        Guid.CreateVersion7(),
        name,
        mode,
        Market.Spot,
        "BTCUSDT",
        "USDT",
        Timeframe.OneHour,
        1000m,
        new AgentRisk(1.5m, 1.75m, 4, 2.5m, 5m, 20m, 0.8m),
        AgentState.Running,
        AgentStateCause.Created,
        Morning,
        Morning,
        StructureCanChange: false,
        HasOpenPosition: false,
        Roles: [],
        ReassignsRoles: false,
        RolesReassignedAt: null);

    /// <summary>A cycle of an agent, in the mode that the agent is in, as the dashboard reads the latest of a mode.</summary>
    private static RecentCycle Decision(AgentView agent, DateTimeOffset closedAt, CycleKind kind = CycleKind.Scheduled) =>
        new(agent.Id, new CycleListing(Guid.CreateVersion7(), closedAt, agent.Mode, closedAt, closedAt.AddSeconds(20), CycleOutcome.Approved, kind));
}
