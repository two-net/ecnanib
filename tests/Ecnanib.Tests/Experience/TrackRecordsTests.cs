using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// The system maintains a track record per provider and per model, filterable by role, market,
/// timeframe, mode, and period (FR-EXP-2). Demo and Live experience are kept and scored
/// separately, and what is not told a mode uses Live data where there is enough, otherwise Demo
/// data, and says which was used (FR-EXP-9). See docs/experience.md.
/// </summary>
/// <remarks>
/// The file of these tests takes 3 trades as the minimum sample, so that a test has enough of
/// them with a few. The clock stands at the morning of the tests, and the trades were closed
/// before it.
/// </remarks>
public class TrackRecordsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Gemini = "gemini-test-model";
    private const string OtherClaude = "claude-other-model";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_role_a_trade_counts_once_for_each_model_and_once_for_each_provider_of_its_team()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));

        // The team of the first trade: one model of Claude as both analysts, a model of Gemini
        // as the Strategist, and another model of Claude as the Risk Manager.
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "gemini", Gemini);
        await application.GiveRoleAsync(user.Id, agent, Role.RiskManager, Morning, "claude", OtherClaude);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), resultInR: 2m);

        var view = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None, Cancellation);

        // The trade counts once for Claude, which held three roles with two models, and once for
        // each of those models, of which one held two roles.
        Assert.Equal([("claude", "Claude", 1, 2m), ("gemini", "Gemini", 1, 2m)], Providers(view));
        Assert.Equal([(OtherClaude, 1, 2m), (StoredAgentRows.TeamModel, 1, 2m), (Gemini, 1, 2m)], Models(view));

        // The team of the second trade is one model in every role.
        await application.GiveTradeAsync(
            user.Id, agent, Morning.AddHours(-2), resultInR: -1m, roles: [.. Enum.GetValues<Role>().Select(role => (role, claude))]);

        view = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None, Cancellation);

        // By provider, and then by identifier: never by result.
        Assert.Equal([("claude", "Claude", 2, 1m), ("gemini", "Gemini", 1, 2m)], Providers(view));
        Assert.Equal([(OtherClaude, 1, 2m), (StoredAgentRows.TeamModel, 2, 1m), (Gemini, 1, 2m)], Models(view));

        // The view counts two trades, whatever its rows add up to.
        Assert.Equal((TradingMode.Demo, 2, 0, 2), (view.Scope.Mode, view.Scope.DemoTrades, view.Scope.LiveTrades, view.Scope.Trades));
    }

    [Fact]
    public async Task With_a_role_a_trade_counts_for_the_model_that_held_that_role_alone()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "gemini", Gemini);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), resultInR: 2m);
        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "claude", StoredAgentRows.TeamModel);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-2), resultInR: -1m);

        // A trade whose Strategist had no model: it has a Risk Manager alone.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-1), resultInR: 5m, roles: [(Role.RiskManager, claude)]);

        var strategists = await application.TrackRecords.OfUserAsync(
            user.Id, TrackRecordFilter.None with { Role = Role.Strategist }, Cancellation);

        Assert.Equal([("claude", "Claude", 1, -1m), ("gemini", "Gemini", 1, 2m)], Providers(strategists));
        Assert.Equal([(StoredAgentRows.TeamModel, 1, -1m), (Gemini, 1, 2m)], Models(strategists));

        // The trade that had no Strategist counts for nobody, and is not among the trades of the view.
        Assert.Equal(2, strategists.Scope.DemoTrades);

        var riskManagers = await application.TrackRecords.OfUserAsync(
            user.Id, TrackRecordFilter.None with { Role = Role.RiskManager }, Cancellation);

        Assert.Equal([("claude", "Claude", 3, 6m)], Providers(riskManagers));
        Assert.Equal([(StoredAgentRows.TeamModel, 3, 6m)], Models(riskManagers));
    }

    [Fact]
    public async Task The_record_is_filtered_by_market_timeframe_and_period()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-400));

        // One trade for each of what a filter tells apart, each with a result of its own.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-1), resultInR: 1m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-2), resultInR: 2m, market: Market.Futures);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-3), resultInR: 4m, timeframe: Timeframe.FourHours);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-7), resultInR: 8m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-7).AddSeconds(-1), resultInR: 16m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddDays(-200), resultInR: 32m);

        async Task<(int Trades, decimal? TotalR, DateTimeOffset? ClosedFrom)> OfAsync(TrackRecordFilter filter)
        {
            var view = await application.TrackRecords.OfUserAsync(user.Id, filter, Cancellation);
            var record = view.Providers.SingleOrDefault()?.Record;

            return (record?.Trades ?? 0, record?.TotalR, view.Scope.ClosedFrom);
        }

        Assert.Equal((6, 63m, null), await OfAsync(TrackRecordFilter.None));
        Assert.Equal((1, 2m, null), await OfAsync(TrackRecordFilter.None with { Market = Market.Futures }));
        Assert.Equal((5, 61m, null), await OfAsync(TrackRecordFilter.None with { Market = Market.Spot }));
        Assert.Equal((1, 4m, null), await OfAsync(TrackRecordFilter.None with { Timeframe = Timeframe.FourHours }));
        Assert.Equal((0, null, null), await OfAsync(TrackRecordFilter.None with { Market = Market.Futures, Timeframe = Timeframe.FourHours }));

        // A period reaches back from now, by when a trade was closed, and the moment that it
        // starts at is included.
        Assert.Equal((4, 15m, Morning.AddDays(-7)), await OfAsync(TrackRecordFilter.None with { Period = TrackPeriod.Last7Days }));
        Assert.Equal((5, 31m, Morning.AddDays(-30)), await OfAsync(TrackRecordFilter.None with { Period = TrackPeriod.Last30Days }));
        Assert.Equal((5, 31m, Morning.AddDays(-90)), await OfAsync(TrackRecordFilter.None with { Period = TrackPeriod.Last90Days }));
        Assert.Equal((6, 63m, Morning.AddDays(-365)), await OfAsync(TrackRecordFilter.None with { Period = TrackPeriod.Last365Days }));
        Assert.Equal(
            (2, 9m, Morning.AddDays(-7)),
            await OfAsync(TrackRecordFilter.None with { Period = TrackPeriod.Last7Days, Market = Market.Spot, Timeframe = Timeframe.OneHour }));
    }

    [Fact]
    public async Task Demo_and_Live_are_never_added_up()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));

        // What the agent did in Demo, and what it did in Live after it was switched.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), resultInR: 1m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-4), resultInR: 1.5m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), resultInR: -3m, mode: TradingMode.Live);

        var demo = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None with { Mode = TradingMode.Demo }, Cancellation);
        var live = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None with { Mode = TradingMode.Live }, Cancellation);

        Assert.Equal([("claude", "Claude", 2, 2.5m)], Providers(demo));
        Assert.Equal([("claude", "Claude", 1, -3m)], Providers(live));
        Assert.Equal([(StoredAgentRows.TeamModel, 2, 2.5m)], Models(demo));
        Assert.Equal([(StoredAgentRows.TeamModel, 1, -3m)], Models(live));

        // Each view says how many trades either mode has, and counts those of its own.
        Assert.Equal((TradingMode.Demo, false, 1, 2, 2), (demo.Scope.Mode, demo.Scope.ModeChosen, demo.Scope.LiveTrades, demo.Scope.DemoTrades, demo.Scope.Trades));
        Assert.Equal((TradingMode.Live, false, 1, 2, 1), (live.Scope.Mode, live.Scope.ModeChosen, live.Scope.LiveTrades, live.Scope.DemoTrades, live.Scope.Trades));

        // So does the record of the model by role.
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        var ofModelInLive = await application.TrackRecords.OfModelAsync(
            user.Id, model, TrackRecordFilter.None with { Mode = TradingMode.Live }, Cancellation);

        Assert.All(ofModelInLive.Roles, role => Assert.Equal((1, -3m), (role.Record.Trades, role.Record.TotalR)));
    }

    [Fact]
    public async Task When_no_mode_is_asked_for_Live_is_used_where_it_has_enough_trades_and_Demo_otherwise_and_the_view_says_which()
    {
        using var application = Application(minSamples: 3);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));

        foreach (var hour in Enumerable.Range(1, 5))
        {
            await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-10 - hour), resultInR: 1m);
        }

        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), resultInR: -1m, mode: TradingMode.Live);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-4), resultInR: -1m, mode: TradingMode.Live, market: Market.Futures);

        // A trade in Live whose result is not recorded yet is no sample.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), mode: TradingMode.Live, recorded: false);

        // Live has 2 trades, fewer than the 3 that are enough: Demo is used, and the view says
        // that it was chosen.
        var view = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None, Cancellation);

        Assert.Equal(
            (TradingMode.Demo, true, 2, 5, 3),
            (view.Scope.Mode, view.Scope.ModeChosen, view.Scope.LiveTrades, view.Scope.DemoTrades, view.Scope.MinSamples));
        Assert.Equal([("claude", "Claude", 5, 5m)], Providers(view));

        // With a third, Live has enough.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-2), resultInR: -1m, mode: TradingMode.Live);

        view = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None, Cancellation);

        Assert.Equal((TradingMode.Live, true, 3, 5), (view.Scope.Mode, view.Scope.ModeChosen, view.Scope.LiveTrades, view.Scope.DemoTrades));
        Assert.Equal([("claude", "Claude", 3, -3m)], Providers(view));

        // The trades that are enough are those that the view would count: with a filter that
        // leaves Live two of them, Demo is used again.
        view = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None with { Market = Market.Spot }, Cancellation);

        Assert.Equal((TradingMode.Demo, true, 2, 5), (view.Scope.Mode, view.Scope.ModeChosen, view.Scope.LiveTrades, view.Scope.DemoTrades));

        // A mode that is asked for is shown, whatever it has, and is not said to be chosen.
        view = await application.TrackRecords.OfUserAsync(
            user.Id, TrackRecordFilter.None with { Market = Market.Futures, Mode = TradingMode.Live }, Cancellation);

        Assert.Equal((TradingMode.Live, false, 1, 0), (view.Scope.Mode, view.Scope.ModeChosen, view.Scope.LiveTrades, view.Scope.DemoTrades));
        Assert.Equal([("claude", "Claude", 1, -1m)], Providers(view));

        // So is the record of a model.
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        var ofModel = await application.TrackRecords.OfModelAsync(user.Id, model, TrackRecordFilter.None, Cancellation);

        Assert.Equal((TradingMode.Live, true, 3, 5), (ofModel.Scope.Mode, ofModel.Scope.ModeChosen, ofModel.Scope.LiveTrades, ofModel.Scope.DemoTrades));
    }

    [Fact]
    public async Task A_record_of_fewer_trades_than_the_minimum_sample_is_marked_and_an_estimate_is_counted()
    {
        using var application = Application(minSamples: 3);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        var gemini = await application.GiveModelAsync("gemini", Gemini, Morning);

        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-4), resultInR: 1m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), resultInR: -2m, complete: false);
        await application.GiveTradeAsync(
            user.Id, agent, Morning.AddHours(-2), resultInR: 1m, roles: [(Role.Strategist, gemini), (Role.RiskManager, claude)]);

        var view = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None, Cancellation);

        Assert.Equal(
            [(StoredAgentRows.TeamModel, 3, false, 1), (Gemini, 1, true, 0)],
            view.Models.Select(model => (model.Model.Identifier, model.Record.Trades, model.Record.SmallSample, model.Record.Estimates)));
    }

    [Fact]
    public async Task The_record_of_a_model_says_each_role_that_it_held_and_any_role_once()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        var gemini = await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "gemini", Gemini);
        var other = await application.GiveRoleAsync(user.Id, agent, Role.RiskManager, Morning, "claude", OtherClaude);

        // The model of Claude is both analysts of the first trade, and the Strategist of the second.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), resultInR: 2m);
        await application.GiveTradeAsync(
            user.Id, agent, Morning.AddHours(-2), resultInR: -1m, roles: [(Role.Strategist, claude), (Role.RiskManager, other)]);

        static IEnumerable<(Role? Role, int Trades, decimal? TotalR)> ByRole(ModelTrackRecordView view) =>
            view.Roles.Select(role => (role.Role, role.Record.Trades, role.Record.TotalR));

        // In any role first, where a trade in which it held two roles counts once, and then in
        // each role, in the order of the roles.
        var ofClaude = await application.TrackRecords.OfModelAsync(user.Id, claude, TrackRecordFilter.None, Cancellation);

        Assert.Equal(
            [(null, 2, 1m), (Role.MarketAnalyst, 1, 2m), (Role.SentimentAnalyst, 1, 2m), (Role.Strategist, 1, -1m), (Role.RiskManager, 0, 0m)],
            ByRole(ofClaude));
        Assert.Equal(2, ofClaude.Scope.Trades);

        var ofGemini = await application.TrackRecords.OfModelAsync(user.Id, gemini, TrackRecordFilter.None, Cancellation);

        Assert.Equal(
            [(null, 1, 2m), (Role.MarketAnalyst, 0, 0m), (Role.SentimentAnalyst, 0, 0m), (Role.Strategist, 1, 2m), (Role.RiskManager, 0, 0m)],
            ByRole(ofGemini));

        // The role of a filter is not gone by: the record of a model says every role.
        Assert.Equal(
            ByRole(ofClaude),
            ByRole(await application.TrackRecords.OfModelAsync(
                user.Id, claude, TrackRecordFilter.None with { Role = Role.RiskManager }, Cancellation)));

        // A model that held no role in a trade of the user has records of no trades.
        var unused = await application.GiveModelAsync("grok", "grok-test-model", Morning);
        var ofUnused = await application.TrackRecords.OfModelAsync(user.Id, unused, TrackRecordFilter.None, Cancellation);

        Assert.Equal(0, ofUnused.Scope.Trades);
        Assert.All(ofUnused.Roles, role => Assert.Equal(0, role.Record.Trades));
        Assert.Equal(5, ofUnused.Roles.Count);
    }

    [Fact]
    public async Task Trades_that_have_no_result_yet_or_no_R_are_in_no_record_and_the_view_says_how_many()
    {
        using var application = Application();
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2));
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-6), resultInR: 1m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-5), recorded: false);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-4), recorded: false);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-3), resultInR: null);

        // Those of the other mode are not those of this view.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(-2), mode: TradingMode.Live, recorded: false);

        var view = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None, Cancellation);

        Assert.Equal([("claude", "Claude", 1, 1m)], Providers(view));
        Assert.Equal((TradingMode.Demo, 1, 2, 1), (view.Scope.Mode, view.Scope.Trades, view.Scope.AwaitingResult, view.Scope.WithoutR));

        var ofModel = await application.TrackRecords.OfModelAsync(user.Id, model, TrackRecordFilter.None, Cancellation);

        Assert.Equal((1, 2, 1), (ofModel.Scope.Trades, ofModel.Scope.AwaitingResult, ofModel.Scope.WithoutR));

        var live = await application.TrackRecords.OfUserAsync(user.Id, TrackRecordFilter.None with { Mode = TradingMode.Live }, Cancellation);

        Assert.Empty(live.Providers);
        Assert.Empty(live.Models);
        Assert.Equal((0, 1, 0), (live.Scope.Trades, live.Scope.AwaitingResult, live.Scope.WithoutR));
    }

    [Fact]
    public async Task Nothing_of_the_trades_of_another_user_is_in_a_record()
    {
        using var application = Application();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Both use the same model, which the catalog has once, for everyone.
        var model = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        await application.GiveTradeAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2)), Morning.AddHours(-3), resultInR: 2m);
        await application.GiveTradeAsync(grace.Id, await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2)), Morning.AddHours(-2), resultInR: -1m);
        await application.GiveTradeAsync(
            grace.Id, await application.GiveAgentAsync(grace.Id, Morning.AddDays(-2), name: "Second", symbol: "ETHUSDT"), Morning.AddHours(-1), resultInR: -1m);

        Assert.Equal([(StoredAgentRows.TeamModel, 1, 2m)], Models(await application.TrackRecords.OfUserAsync(ada.Id, TrackRecordFilter.None, Cancellation)));
        Assert.Equal([(StoredAgentRows.TeamModel, 2, -2m)], Models(await application.TrackRecords.OfUserAsync(grace.Id, TrackRecordFilter.None, Cancellation)));

        var ofAda = await application.TrackRecords.OfModelAsync(ada.Id, model, TrackRecordFilter.None, Cancellation);
        var ofGrace = await application.TrackRecords.OfModelAsync(grace.Id, model, TrackRecordFilter.None, Cancellation);

        Assert.Equal((1, 2m), (ofAda.Roles[0].Record.Trades, ofAda.Roles[0].Record.TotalR));
        Assert.Equal((2, -2m), (ofGrace.Roles[0].Record.Trades, ofGrace.Roles[0].Record.TotalR));

        // A user who has no trade has no record.
        var nobody = await application.TrackRecords.OfUserAsync(Guid.CreateVersion7(), TrackRecordFilter.None, Cancellation);

        Assert.Empty(nobody.Providers);
        Assert.Empty(nobody.Models);
        Assert.Equal((TradingMode.Demo, 0), (nobody.Scope.Mode, nobody.Scope.Trades));
    }

    /// <summary>The application, with a minimum sample that a test reaches with a few trades.</summary>
    private static AccountsApplication Application(int minSamples = 3) =>
        AgentsApplication.With(
            new StandInMarketData(), new TestClock(Morning), settings: file => file.With("Experience:MinSamples", minSamples));

    private static IEnumerable<(string Code, string Name, int Trades, decimal? TotalR)> Providers(TrackRecordView view) =>
        view.Providers.Select(provider => (provider.Provider.Value, provider.ProviderName, provider.Record.Trades, provider.Record.TotalR));

    private static IEnumerable<(string Identifier, int Trades, decimal? TotalR)> Models(TrackRecordView view) =>
        view.Models.Select(model => (model.Model.Identifier, model.Record.Trades, model.Record.TotalR));
}
