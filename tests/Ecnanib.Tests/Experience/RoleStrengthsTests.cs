using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// A role-specific measure is kept for each role (FR-EXP-3), and a strength score per model,
/// role, market, and timeframe is that measure damped toward zero for small samples: measure ×
/// n ÷ (n + damping). Slices with fewer samples than the minimum show "not enough data"
/// (FR-EXP-4). Demo and Live are scored separately (FR-EXP-9). See "The strength score" in
/// docs/experience.md.
/// </summary>
public class RoleStrengthsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Claude = StoredAgentRows.TeamModel;
    private const string Gemini = "gemini-test-model";

    // The application of these tests damps by 4 and takes 4 samples as enough, so that a slice
    // of 4 samples has half of its measure as its score, and one of 12 three quarters.
    private const int Damping = 4;
    private const int MinSamples = 4;

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_strength_of_an_analyst_is_from_its_scored_calls()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);

        // The Market Analyst was right 3 times of 4, and the Sentiment Analyst 3 times of 12.
        // Two more calls are not scored yet, and one could not be: none of them is a sample.
        await application.GiveCallsAsync(
            user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 3, missed: 1, awaiting: 2, withoutCandles: 1);
        await application.GiveCallsAsync(user.Id, agent, Role.SentimentAnalyst, gemini, Morning.AddHours(-5), matched: 3, missed: 9);

        var view = await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        Assert.Equal((TradingMode.Demo, MinSamples, Damping), (view.Mode, view.MinSamples, view.Damping));

        // 75 percent is 25 points above a half, and 4 samples of 8 give half of it. 25 percent
        // is 25 points below, and 12 samples of 16 give three quarters of that.
        Assert.Equal(
            [
                (Claude, Role.MarketAnalyst, Market.Spot, Timeframe.OneHour, 4, 0.25m, 0.125m),
                (Gemini, Role.SentimentAnalyst, Market.Spot, Timeframe.OneHour, 12, -0.25m, -0.1875m),
            ],
            Slices(view));
        Assert.All(view.Slices, slice => Assert.True(slice.Enough));

        // What is left out is said.
        Assert.Equal((2, 1), (view.CallsAwaiting, view.CallsWithoutCandles));
        Assert.Equal((0, 0), (view.ShadowTradesOpen, view.ShadowTradesWithoutResult));
    }

    [Fact]
    public async Task The_strength_of_the_Strategist_is_from_the_average_R_of_its_executed_plans()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);
        (Role, Guid)[] team = [(Role.Strategist, gemini), (Role.RiskManager, claude)];

        // Four plans of the Strategist were executed: 2 R, -1 R, 1 R, and nothing. A fifth is
        // closed with no result yet, and a sixth has a result that says no R: neither is a sample.
        foreach (var (result, hour) in new[] { 2m, -1m, 1m, 0m }.Select((result, hour) => (result, hour)))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-10 + hour), resultInR: result, roles: team);
        }

        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-2), recorded: false, roles: team);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-1), resultInR: null, roles: team);

        var view = await application.RoleStrengths.OfUserAsync(
            user.Id, TrackRecordFilter.None with { Role = Role.Strategist }, TradingMode.Demo, Cancellation);

        // 2 R over 4 plans is 0.5 R, and 4 samples of 8 give half of it.
        Assert.Equal([(Gemini, Role.Strategist, Market.Spot, Timeframe.OneHour, 4, 0.5m, 0.25m)], Slices(view));
    }

    [Fact]
    public async Task The_strength_of_the_Risk_Manager_is_from_what_it_approved_minus_what_it_vetoed()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);
        (Role, Guid)[] team = [(Role.Strategist, gemini), (Role.RiskManager, claude)];

        // It approved four plans, which made 0.5 R on average.
        foreach (var (result, hour) in new[] { 1m, 1m, -1m, 1m }.Select((result, hour) => (result, hour)))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-20 + hour), resultInR: result, roles: team);
        }

        var filter = TrackRecordFilter.None with { Role = Role.RiskManager };

        // While nothing that it vetoed has a result, there is nothing to take away: the slice
        // has its sample, and neither a measure nor a score.
        var approvedOnly = Assert.Single((await application.RoleStrengths.OfUserAsync(user.Id, filter, TradingMode.Demo, Cancellation)).Slices);

        Assert.Equal((4, null, true, null), (approvedOnly.Measure.Samples, approvedOnly.Measure.Value, approvedOnly.Enough, approvedOnly.Score));
        Assert.Equal(new ReviewedPlans(Approved: 4, Vetoed: 0), approvedOnly.Measure.Reviewed);

        // It vetoed two plans that would have lost 1 R each. One more is open still, and one
        // was never entered: neither has a result, and neither is a sample.
        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-9), resultInR: -1.2m);
        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-8), resultInR: -0.8m);
        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-7), open: true);
        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-6), resultInR: null, outcome: ShadowOutcome.NotEntered);

        var view = await application.RoleStrengths.OfUserAsync(user.Id, filter, TradingMode.Demo, Cancellation);

        // 0.5 R less -1 R is 1.5 R, of 6 plans with a result, and 6 samples of 10 give six tenths of it.
        Assert.Equal([(Claude, Role.RiskManager, Market.Spot, Timeframe.OneHour, 6, 1.5m, 0.9m)], Slices(view));
        Assert.Equal(new ReviewedPlans(Approved: 4, Vetoed: 2), Assert.Single(view.Slices).Measure.Reviewed);
        Assert.Equal((1, 1), (view.ShadowTradesOpen, view.ShadowTradesWithoutResult));

        // A shadow trade counts for the model that vetoed the plan, whoever is the Risk Manager now.
        await application.GiveShadowTradeAsync(user.Id, agent, gemini, Morning.AddHours(-5), resultInR: 2m);

        var both = await application.RoleStrengths.OfUserAsync(user.Id, filter, TradingMode.Demo, Cancellation);

        // The other model vetoed one plan, and approved none: it has a sample of one, and no measure.
        Assert.Equal(
            [(Claude, 6, (decimal?)1.5m), (Gemini, 1, null)],
            both.Slices.Select(slice => (slice.Model.Identifier, slice.Measure.Samples, slice.Measure.Value)));
    }

    [Fact]
    public async Task A_slice_below_the_minimum_sample_says_so_and_has_no_score()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);

        // One call short of the minimum sample, and all of them right.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: MinSamples - 1);

        var few = Assert.Single((await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation)).Slices);

        // It says its sample and its measure, and that it has not enough data. It has no score,
        // so nothing can recommend from it.
        Assert.Equal((3, 0.5m, false, null), (few.Measure.Samples, few.Measure.Value, few.Enough, few.Score));

        // One more call, and it has one.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddDays(-1), matched: 1);

        var enough = Assert.Single((await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation)).Slices);

        Assert.Equal((4, 0.5m, true, 0.25m), (enough.Measure.Samples, enough.Measure.Value, enough.Enough, enough.Score));
    }

    [Fact]
    public async Task A_slice_is_of_one_role_one_market_and_one_timeframe_and_the_filter_chooses_among_them()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var hourly = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Hourly");
        var futures = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-2), name: "Futures", market: Market.Futures, timeframe: Timeframe.FourHours);
        var claude = await application.GiveModelAsync("claude", Claude, Morning);

        // One model was the Market Analyst of two agents, and the Sentiment Analyst of one: it
        // is right on Spot on the hour, and wrong on Futures on 4 hours.
        await application.GiveCallsAsync(user.Id, hourly, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 4);
        await application.GiveCallsAsync(user.Id, futures, Role.MarketAnalyst, claude, Morning.AddHours(-5), missed: 4);
        await application.GiveCallsAsync(user.Id, futures, Role.SentimentAnalyst, claude, Morning.AddHours(-5), matched: 2, missed: 2);

        Task<RoleStrengthView> Of(TrackRecordFilter filter) =>
            application.RoleStrengths.OfUserAsync(user.Id, filter, TradingMode.Demo, Cancellation);

        // Nothing is added up over them: each is a slice of its own, in the order of the roles,
        // and then of the markets and the timeframes.
        Assert.Equal(
            [
                (Claude, Role.MarketAnalyst, Market.Spot, Timeframe.OneHour, 4, 0.5m, 0.25m),
                (Claude, Role.MarketAnalyst, Market.Futures, Timeframe.FourHours, 4, -0.5m, -0.25m),
                (Claude, Role.SentimentAnalyst, Market.Futures, Timeframe.FourHours, 4, 0m, 0m),
            ],
            Slices(await Of(TrackRecordFilter.None)));

        // What is chosen leaves the other slices out.
        Assert.Equal(
            [(Role.MarketAnalyst, Market.Futures), (Role.SentimentAnalyst, Market.Futures)],
            (await Of(TrackRecordFilter.None with { Market = Market.Futures })).Slices.Select(slice => (slice.Role, slice.Market)));
        Assert.Equal(
            [(Role.MarketAnalyst, Timeframe.OneHour)],
            (await Of(TrackRecordFilter.None with { Timeframe = Timeframe.OneHour })).Slices.Select(slice => (slice.Role, slice.Timeframe)));
        Assert.Equal(
            [(Role.SentimentAnalyst, Market.Futures)],
            (await Of(TrackRecordFilter.None with { Role = Role.SentimentAnalyst })).Slices.Select(slice => (slice.Role, slice.Market)));
        Assert.Empty((await Of(TrackRecordFilter.None with { Role = Role.Strategist })).Slices);
        Assert.Empty((await Of(TrackRecordFilter.None with { Market = Market.Spot, Timeframe = Timeframe.FourHours })).Slices);
    }

    [Fact]
    public async Task Demo_and_Live_are_scored_separately_and_never_added_up()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);
        (Role, Guid)[] team = [(Role.Strategist, claude), (Role.RiskManager, claude)];

        // In Demo the model was right, its plans won, and its vetoes kept losses out. In Live
        // it was the other way in everything.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 4, mode: TradingMode.Demo);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), missed: 4, mode: TradingMode.Live);

        foreach (var hour in Enumerable.Range(0, 4))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-20 + hour), resultInR: 1m, mode: TradingMode.Demo, roles: team);
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-20 + hour), resultInR: -1m, mode: TradingMode.Live, roles: team);
            await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-20 + hour), resultInR: -1m, mode: TradingMode.Demo);
            await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-20 + hour), resultInR: 1m, mode: TradingMode.Live);
        }

        var demo = await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation);
        var live = await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Live, Cancellation);

        Assert.Equal(TradingMode.Demo, demo.Mode);
        Assert.Equal(
            [(Role.MarketAnalyst, 4, 0.5m), (Role.Strategist, 4, 1m), (Role.RiskManager, 8, 2m)],
            demo.Slices.Select(slice => (slice.Role, slice.Measure.Samples, slice.Measure.Value!.Value)));

        Assert.Equal(TradingMode.Live, live.Mode);
        Assert.Equal(
            [(Role.MarketAnalyst, 4, -0.5m), (Role.Strategist, 4, -1m), (Role.RiskManager, 8, -2m)],
            live.Slices.Select(slice => (slice.Role, slice.Measure.Samples, slice.Measure.Value!.Value)));

        // The mode of the filter is not gone by: whoever asks says the mode, and it is one of the two.
        var asked = await application.RoleStrengths.OfUserAsync(
            user.Id, TrackRecordFilter.None with { Mode = TradingMode.Live }, TradingMode.Demo, Cancellation);

        Assert.Equal(Slices(demo), Slices(asked));
    }

    [Fact]
    public async Task A_period_counts_what_is_not_older_than_it()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-60));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);
        (Role, Guid)[] team = [(Role.RiskManager, claude)];

        // Calls, approved plans, and vetoed plans of the last days, and of a month ago.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 4);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddDays(-30), missed: 4);

        foreach (var hour in Enumerable.Range(0, 2))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-1).AddHours(hour), resultInR: 1m, roles: team);
            await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-30).AddHours(hour), resultInR: -3m, roles: team);
            await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddDays(-1).AddHours(hour), resultInR: -1m);
            await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddDays(-30).AddHours(hour), resultInR: 3m);
        }

        Task<RoleStrengthView> Of(TrackPeriod period) =>
            application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None with { Period = period }, TradingMode.Demo, Cancellation);

        // Everything: 4 of 8 calls matched, and what was approved made -1 R on average where
        // what was vetoed would have made 1 R.
        Assert.Equal(
            [(Role.MarketAnalyst, 8, 0m), (Role.RiskManager, 8, -2m)],
            (await Of(TrackPeriod.AllTime)).Slices.Select(slice => (slice.Role, slice.Measure.Samples, slice.Measure.Value!.Value)));

        // The last 7 days: a call by the close of its candle, a plan that was executed by when
        // its position was closed, and a vetoed plan by the close of its candle.
        Assert.Equal(
            [(Role.MarketAnalyst, 4, 0.5m), (Role.RiskManager, 4, 2m)],
            (await Of(TrackPeriod.Last7Days)).Slices.Select(slice => (slice.Role, slice.Measure.Samples, slice.Measure.Value!.Value)));

        // The last 90 days reach back to all of them.
        Assert.Equal(
            [(Role.MarketAnalyst, 8), (Role.RiskManager, 8)],
            (await Of(TrackPeriod.Last90Days)).Slices.Select(slice => (slice.Role, slice.Measure.Samples)));
    }

    [Fact]
    public async Task The_slices_of_a_model_are_of_every_role_that_it_has_a_sample_in_and_of_no_other_model()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);
        (Role, Guid)[] team = [(Role.Strategist, gemini), (Role.RiskManager, claude)];

        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 4);
        await application.GiveCallsAsync(user.Id, agent, Role.SentimentAnalyst, gemini, Morning.AddHours(-5), missed: 4);

        foreach (var hour in Enumerable.Range(0, 4))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-20 + hour), resultInR: 1m, roles: team);
        }

        await application.GiveShadowTradeAsync(user.Id, agent, claude, Morning.AddHours(-9), resultInR: -1m);
        await application.GiveShadowTradeAsync(user.Id, agent, gemini, Morning.AddHours(-8), resultInR: 1m);

        // The role of the filter is not gone by: the page of a model says every role.
        var ofClaude = await application.RoleStrengths.OfModelAsync(
            user.Id, claude, TrackRecordFilter.None with { Role = Role.Strategist }, TradingMode.Demo, Cancellation);

        Assert.Equal(
            [(Claude, Role.MarketAnalyst, 4, (decimal?)0.5m), (Claude, Role.RiskManager, 5, 2m)],
            ofClaude.Slices.Select(slice => (slice.Model.Identifier, slice.Role, slice.Measure.Samples, slice.Measure.Value)));

        var ofGemini = await application.RoleStrengths.OfModelAsync(user.Id, gemini, TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        Assert.Equal(
            [(Gemini, Role.SentimentAnalyst, 4, (decimal?)-0.5m), (Gemini, Role.Strategist, 4, 1m), (Gemini, Role.RiskManager, 1, null)],
            ofGemini.Slices.Select(slice => (slice.Model.Identifier, slice.Role, slice.Measure.Samples, slice.Measure.Value)));

        // A model that nothing of the user counts for has no slice.
        var unused = await application.GiveModelAsync("claude", "claude-other-model", Morning);

        Assert.Empty((await application.RoleStrengths.OfModelAsync(user.Id, unused, TrackRecordFilter.None, TradingMode.Demo, Cancellation)).Slices);
    }

    [Fact]
    public async Task The_slices_are_in_the_order_of_the_providers_and_the_identifiers_and_never_of_a_score()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", Claude, Morning);
        var otherClaude = await application.GiveModelAsync("claude", "claude-other-model", Morning);
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);

        // The best is of the provider that is listed last, and the worst is listed first.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, gemini, Morning.AddHours(-5), matched: 4);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 2, missed: 2);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, otherClaude, Morning.AddHours(-5), missed: 4);

        var view = await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        Assert.Equal(
            [("claude-other-model", -0.25m), (Claude, 0m), (Gemini, 0.25m)],
            view.Slices.Select(slice => (slice.Model.Identifier, slice.Score!.Value)));
    }

    [Fact]
    [ProvesOwnershipOf("analyst_calls", "shadow_trades")]
    public async Task A_user_is_answered_with_nothing_of_what_the_agents_of_another_user_did()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Both use the same model, which the catalog has once, for everyone.
        var model = await application.GiveModelAsync("claude", Claude, Morning);
        var adas = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2));
        var graces = await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2));
        (Role, Guid)[] team = [(Role.RiskManager, model)];

        // Ada's analyst was right, and her Risk Manager kept losses out. Grace's analyst was
        // wrong, more often, and her Risk Manager kept gains out.
        await application.GiveCallsAsync(ada.Id, adas, Role.MarketAnalyst, model, Morning.AddHours(-5), matched: 4, awaiting: 1);
        await application.GiveCallsAsync(grace.Id, graces, Role.MarketAnalyst, model, Morning.AddHours(-5), missed: 12, awaiting: 3, withoutCandles: 2);

        foreach (var hour in Enumerable.Range(0, 2))
        {
            await application.GiveTradeAsync(ada.Id, adas, Morning.AddHours(-20 + hour), resultInR: 1m, roles: team);
            await application.GiveShadowTradeAsync(ada.Id, adas, model, Morning.AddHours(-20 + hour), resultInR: -1m);
            await application.GiveTradeAsync(grace.Id, graces, Morning.AddHours(-20 + hour), resultInR: -1m, roles: team);
            await application.GiveShadowTradeAsync(grace.Id, graces, model, Morning.AddHours(-20 + hour), resultInR: 3m);
        }

        await application.GiveShadowTradeAsync(grace.Id, graces, model, Morning.AddHours(-3), open: true);

        var ofAda = await application.RoleStrengths.OfUserAsync(ada.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation);
        var ofGrace = await application.RoleStrengths.OfUserAsync(grace.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        // Each is answered with her own, and nothing of the other is in a sample or in a count.
        Assert.Equal(
            [(Role.MarketAnalyst, 4, 0.5m), (Role.RiskManager, 4, 2m)],
            ofAda.Slices.Select(slice => (slice.Role, slice.Measure.Samples, slice.Measure.Value!.Value)));
        Assert.Equal((1, 0, 0, 0), (ofAda.CallsAwaiting, ofAda.CallsWithoutCandles, ofAda.ShadowTradesOpen, ofAda.ShadowTradesWithoutResult));

        Assert.Equal(
            [(Role.MarketAnalyst, 12, -0.5m), (Role.RiskManager, 4, -4m)],
            ofGrace.Slices.Select(slice => (slice.Role, slice.Measure.Samples, slice.Measure.Value!.Value)));
        Assert.Equal((3, 2, 1, 0), (ofGrace.CallsAwaiting, ofGrace.CallsWithoutCandles, ofGrace.ShadowTradesOpen, ofGrace.ShadowTradesWithoutResult));

        // So is it on the page of the model, which has one address for everyone.
        var modelOfAda = await application.RoleStrengths.OfModelAsync(ada.Id, model, TrackRecordFilter.None, TradingMode.Demo, Cancellation);
        var modelOfGrace = await application.RoleStrengths.OfModelAsync(grace.Id, model, TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        Assert.Equal(Slices(ofAda), Slices(modelOfAda));
        Assert.Equal(Slices(ofGrace), Slices(modelOfGrace));

        // A user who has nothing has no slice.
        var nobody = await application.RoleStrengths.OfUserAsync(Guid.CreateVersion7(), TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        Assert.Empty(nobody.Slices);
        Assert.Equal((0, 0, 0, 0), (nobody.CallsAwaiting, nobody.CallsWithoutCandles, nobody.ShadowTradesOpen, nobody.ShadowTradesWithoutResult));
    }

    [Fact]
    public async Task What_an_agent_left_goes_with_it_and_out_of_every_slice()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var kept = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Kept");
        var deleted = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Deleted", symbol: "SOLUSDT");
        var claude = await application.GiveModelAsync("claude", Claude, Morning);

        await application.GiveCallsAsync(user.Id, kept, Role.MarketAnalyst, claude, Morning.AddHours(-5), matched: 4);
        await application.GiveCallsAsync(user.Id, deleted, Role.MarketAnalyst, claude, Morning.AddHours(-5), missed: 4);
        await application.GiveShadowTradeAsync(user.Id, deleted, claude, Morning.AddHours(-9));

        var before = await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        Assert.Equal(
            [(Role.MarketAnalyst, 8), (Role.RiskManager, 1)],
            before.Slices.Select(slice => (slice.Role, slice.Measure.Samples)));

        // The calls and the shadow trades of an agent hang on its cycles, which are deleted
        // with it. So a slice says what the stored cycles say, as a track record does of the positions.
        await application.DeleteAgentAsync(deleted);

        var after = await application.RoleStrengths.OfUserAsync(user.Id, TrackRecordFilter.None, TradingMode.Demo, Cancellation);

        Assert.Equal([(Claude, Role.MarketAnalyst, Market.Spot, Timeframe.OneHour, 4, 0.5m, 0.25m)], Slices(after));
        Assert.Equal(0, application.Database.Scalar<long>("SELECT count(*) FROM shadow_trades"));
    }

    /// <summary>The application, with a minimum sample and a damping that a test reaches with a few rows.</summary>
    private static AccountsApplication Application() =>
        AgentsApplication.With(
            new StandInMarketData(),
            new TestClock(Morning),
            settings: file => file.With("Experience:MinSamples", MinSamples).With("Experience:Damping", Damping));

    /// <summary>What a slice says: whose it is, how many samples it has, its measure, and its score.</summary>
    private static IEnumerable<(string Identifier, Role Role, Market Market, Timeframe Timeframe, int Samples, decimal? Measure, decimal? Score)> Slices(
        RoleStrengthView view) =>
        view.Slices.Select(slice => (
            slice.Model.Identifier, slice.Role, slice.Market, slice.Timeframe, slice.Measure.Samples, slice.Measure.Value, slice.Score));
}
