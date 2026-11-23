using Ecnanib.Agents;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The closed positions of a user are the trades that a track record counts: each with its
/// result in R, how long it was held, and the model that held each role when it was opened
/// (FR-EXP-1, FR-EXP-2). Experience asks for them through <see cref="IClosedTrades"/>, which the
/// Decision Engine implements. See "The trades" in docs/experience.md.
/// </summary>
public class ClosedPositionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_closed_position_is_a_trade_with_its_result_and_the_model_of_each_role()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, market: Market.Futures, timeframe: Timeframe.FourHours);
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);
        var gemini = await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, "gemini", "gemini-test-model");

        var position = await application.GiveTradeAsync(
            user.Id, agent, Morning.AddHours(2), resultInR: -1.07m, holdingSeconds: 1800, complete: false);

        var trade = Assert.Single(await application.ClosedTrades.OfUserAsync(user.Id, null, TestContext.Current.CancellationToken));

        // What the position says of itself: its mode, its market, its timeframe, and its close.
        Assert.Equal(
            (position, TradingMode.Demo, Market.Futures, Timeframe.FourHours, Morning.AddHours(2)),
            (trade.PositionId, trade.Mode, trade.Market, trade.Timeframe, trade.ClosedAt));

        // Its result, which says that it is an estimate.
        Assert.Equal(new TradeResult(-1.07m, 1800, Complete: false), trade.Result);

        // The model that held each role, in the order of the roles.
        Assert.Equal(
            [
                new TradeRole(Role.MarketAnalyst, claude),
                new TradeRole(Role.SentimentAnalyst, claude),
                new TradeRole(Role.Strategist, gemini),
                new TradeRole(Role.RiskManager, claude),
            ],
            trade.Roles);
    }

    [Fact]
    public async Task An_open_position_is_no_trade()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(1), closed: false);

        Assert.Empty(await application.ClosedTrades.OfUserAsync(user.Id, null, TestContext.Current.CancellationToken));

        // Neither is it one for a period that it was opened in.
        Assert.Empty(await application.ClosedTrades.OfUserAsync(user.Id, Morning.AddDays(-1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_closed_position_without_a_recorded_result_is_a_trade_without_one()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        // A result is recorded some time after the close: until then the position has none.
        var awaiting = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(1), recorded: false);
        var withoutR = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(2), resultInR: null, holdingSeconds: 900);

        var trades = await application.ClosedTrades.OfUserAsync(user.Id, null, TestContext.Current.CancellationToken);

        Assert.Equal([awaiting, withoutR], trades.Select(trade => trade.PositionId));
        Assert.Null(trades[0].Result);
        Assert.Equal(4, trades[0].Roles.Count);

        // A result that says no R is a result all the same: it is told apart from none.
        Assert.Equal(new TradeResult(null, 900, Complete: true), trades[1].Result);
    }

    [Fact]
    public async Task Only_the_trades_that_closed_since_the_moment_asked_for_are_read()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        var first = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(1));
        var second = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(2));
        var third = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(3), recorded: false);

        async Task<IEnumerable<Guid>> SinceAsync(DateTimeOffset? from) =>
            (await application.ClosedTrades.OfUserAsync(user.Id, from, TestContext.Current.CancellationToken)).Select(trade => trade.PositionId);

        // In the order they were closed in.
        Assert.Equal([first, second, third], await SinceAsync(null));

        // The moment itself is included, and it is a moment, in whatever offset it is said.
        Assert.Equal([second, third], await SinceAsync(Morning.AddHours(2)));
        Assert.Equal([second, third], await SinceAsync(Morning.AddHours(2).ToOffset(TimeSpan.FromHours(7))));
        Assert.Equal([third], await SinceAsync(Morning.AddHours(2).AddSeconds(1)));
        Assert.Empty(await SinceAsync(Morning.AddHours(4)));
    }

    [Fact]
    [ProvesOwnershipOf("positions", "position_results", "position_roles")]
    public async Task The_trades_of_another_user_are_not_read()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Both have an agent with the same model in every role, and a closed trade of it.
        var ofAda = await application.GiveTradeAsync(
            ada.Id, await application.GiveAgentAsync(ada.Id, Morning), Morning.AddHours(1), resultInR: 2m);
        var ofGrace = await application.GiveTradeAsync(
            grace.Id, await application.GiveAgentAsync(grace.Id, Morning), Morning.AddHours(1), resultInR: -1m);

        var adas = Assert.Single(await application.ClosedTrades.OfUserAsync(ada.Id, null, TestContext.Current.CancellationToken));
        var graces = Assert.Single(await application.ClosedTrades.OfUserAsync(grace.Id, null, TestContext.Current.CancellationToken));

        // Each reads their own position, its own result, and its own roles.
        Assert.Equal((ofAda, 2m, 4), (adas.PositionId, adas.Result!.ResultInR, adas.Roles.Count));
        Assert.Equal((ofGrace, -1m, 4), (graces.PositionId, graces.Result!.ResultInR, graces.Roles.Count));

        // A user who has no trade reads none.
        Assert.Empty(await application.ClosedTrades.OfUserAsync(Guid.CreateVersion7(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_trade_says_what_its_position_says_and_not_what_its_agent_is_now()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);

        // The agent traded in Demo, on Spot, on the hour. It was switched to Live since, and
        // given another market, another timeframe, and another Strategist.
        var agent = await application.GiveAgentAsync(
            user.Id, Morning, mode: TradingMode.Live, market: Market.Futures, timeframe: Timeframe.OneDay);
        var before = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        await application.GiveTradeAsync(
            user.Id, agent, Morning.AddHours(1), mode: TradingMode.Demo, market: Market.Spot, timeframe: Timeframe.OneHour);
        var after = await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning.AddHours(2), "gemini", "gemini-test-model");

        var trade = Assert.Single(await application.ClosedTrades.OfUserAsync(user.Id, null, TestContext.Current.CancellationToken));

        // What it did in Demo stays Demo (FR-BIN-11), and the model that decided it stays the
        // one that held the role then.
        Assert.Equal((TradingMode.Demo, Market.Spot, Timeframe.OneHour), (trade.Mode, trade.Market, trade.Timeframe));
        Assert.Equal(before, trade.Roles.Single(role => role.Role == Role.Strategist).ModelId);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task A_role_that_had_no_model_is_not_among_the_roles_of_a_trade()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, team: false);

        var none = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(1));
        var model = await application.GiveRoleAsync(user.Id, agent, Role.RiskManager, Morning.AddHours(1));
        var one = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(2));

        var trades = await application.ClosedTrades.OfUserAsync(user.Id, null, TestContext.Current.CancellationToken);

        Assert.Equal([none, one], trades.Select(trade => trade.PositionId));
        Assert.Empty(trades[0].Roles);
        Assert.Equal([new TradeRole(Role.RiskManager, model)], trades[1].Roles);
    }

    [Fact]
    public async Task The_trades_of_a_deleted_agent_go_with_it()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var kept = await application.GiveAgentAsync(user.Id, Morning, name: "Kept");
        var deleted = await application.GiveAgentAsync(user.Id, Morning, name: "Deleted", symbol: "ETHUSDT");

        var stays = await application.GiveTradeAsync(user.Id, kept, Morning.AddHours(1));
        await application.GiveTradeAsync(user.Id, deleted, Morning.AddHours(2));

        Assert.Equal(2, (await application.ClosedTrades.OfUserAsync(user.Id, null, TestContext.Current.CancellationToken)).Count);

        // The positions of an agent are deleted with it, and their results and roles with them:
        // a trade is read from them, and is kept nowhere else.
        await application.DeleteAgentAsync(deleted);

        var trade = Assert.Single(await application.ClosedTrades.OfUserAsync(user.Id, null, TestContext.Current.CancellationToken));

        Assert.Equal(stays, trade.PositionId);
    }

    [Fact]
    public async Task The_trades_of_the_board_are_those_of_every_user_who_has_not_left_it_each_with_whose_it_is()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var claude = await application.GiveModelAsync("claude", StoredAgentRows.TeamModel, Morning);

        var ofAda = await application.GiveTradeAsync(
            ada.Id, await application.GiveAgentAsync(ada.Id, Morning), Morning.AddHours(1), resultInR: 2m, holdingSeconds: 1800, complete: false);
        var ofGrace = await application.GiveTradeAsync(
            grace.Id,
            await application.GiveAgentAsync(grace.Id, Morning, market: Market.Futures, timeframe: Timeframe.FourHours),
            Morning.AddHours(2),
            resultInR: -1m,
            mode: TradingMode.Live);

        // Across users, which IClosedTrades never answers: the board adds them up (FR-EXP-10).
        // In the order they were closed in, each with whose it is and as the trade that a track
        // record counts.
        var trades = await application.BoardTrades.OfContributorsAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal([(ada.Id, ofAda), (grace.Id, ofGrace)], trades.Select(trade => (trade.UserId, trade.Trade.PositionId)));
        Assert.Equal(
            (TradingMode.Demo, Market.Spot, Timeframe.OneHour, Morning.AddHours(1), new TradeResult(2m, 1800, Complete: false)),
            (trades[0].Trade.Mode, trades[0].Trade.Market, trades[0].Trade.Timeframe, trades[0].Trade.ClosedAt, trades[0].Trade.Result));
        Assert.Equal([.. Enum.GetValues<Role>().Select(role => new TradeRole(role, claude))], trades[0].Trade.Roles);
        Assert.Equal(
            (TradingMode.Live, Market.Futures, Timeframe.FourHours, -1m),
            (trades[1].Trade.Mode, trades[1].Trade.Market, trades[1].Trade.Timeframe, trades[1].Trade.Result!.ResultInR));

        // Grace leaves the board: her trade is handed to nothing, and Ada's still is.
        await application.BoardContribution.SetAsync(grace.Id, contributes: false, TestContext.Current.CancellationToken);

        Assert.Equal(
            [(ada.Id, ofAda)],
            (await application.BoardTrades.OfContributorsAsync(null, TestContext.Current.CancellationToken))
                .Select(trade => (trade.UserId, trade.Trade.PositionId)));

        // Her own trades are read for her as before: she left the board, and nothing else.
        Assert.Equal(
            ofGrace,
            Assert.Single(await application.ClosedTrades.OfUserAsync(grace.Id, null, TestContext.Current.CancellationToken)).PositionId);

        // And she comes back.
        await application.BoardContribution.SetAsync(grace.Id, contributes: true, TestContext.Current.CancellationToken);

        Assert.Equal(2, (await application.BoardTrades.OfContributorsAsync(null, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Only_a_closed_position_with_a_result_in_R_is_a_trade_of_the_board()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        // A position that is open still, one whose result is not recorded yet, and one whose
        // result says no R: a record counts none of them, and the board says nothing of them,
        // so none is read for it.
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(1), recorded: false);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(2), resultInR: null);
        var counted = await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(3), resultInR: 0m);
        await application.GiveTradeAsync(user.Id, agent, Morning.AddHours(4), closed: false);

        var trade = Assert.Single(await application.BoardTrades.OfContributorsAsync(null, TestContext.Current.CancellationToken));

        // A trade of exactly 0 R has a result in R.
        Assert.Equal((user.Id, counted, 0m), (trade.UserId, trade.Trade.PositionId, trade.Trade.Result!.ResultInR));
    }

    [Fact]
    public async Task Only_the_trades_of_the_board_that_closed_since_the_moment_asked_for_are_read()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        var first = await application.GiveTradeAsync(ada.Id, await application.GiveAgentAsync(ada.Id, Morning), Morning.AddHours(1));
        var second = await application.GiveTradeAsync(grace.Id, await application.GiveAgentAsync(grace.Id, Morning), Morning.AddHours(2));

        async Task<IEnumerable<Guid>> SinceAsync(DateTimeOffset? from) =>
            (await application.BoardTrades.OfContributorsAsync(from, TestContext.Current.CancellationToken)).Select(trade => trade.Trade.PositionId);

        Assert.Equal([first, second], await SinceAsync(null));

        // The moment itself is included, and it is a moment, in whatever offset it is said.
        Assert.Equal([second], await SinceAsync(Morning.AddHours(2)));
        Assert.Equal([second], await SinceAsync(Morning.AddHours(2).ToOffset(TimeSpan.FromHours(7))));
        Assert.Empty(await SinceAsync(Morning.AddHours(2).AddSeconds(1)));
    }
}
