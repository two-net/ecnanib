using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// In-app notifications cover a position that was opened or closed (FR-NTF-1): the user of an
/// agent is told once when an entry of the agent opens a position, with what it holds and where
/// its protection is, and once when the result of a closed position is recorded, with how it
/// was closed and what it made. See "What is raised" in docs/notifications.md.
/// </summary>
/// <remarks>
/// The agent, its plans, and the numbers are those of <see cref="PositionResultTests"/>: on Spot
/// it buys 0.01 at 60000 with its stop-loss at 59000 and its take-profit at 62000, and risks 10.
/// On Futures it buys or sells 0.02, and risks 20.
/// </remarks>
public class PositionNotificationTests
{
    [Fact]
    public async Task The_user_of_an_agent_is_told_once_that_a_position_was_opened()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // What it holds, at which price, and where its stop-loss and its take-profit are. On
        // the Spot market a position has no side to say.
        var opened = Assert.Single(await application.NotificationsOfAsync(agent.UserId, positions: true));

        Assert.Equal((NotificationType.PositionOpened, (Guid?)agent.Id, false), (opened.Type, opened.AgentId, opened.IsRead));
        Assert.Equal(
            "A position in BTCUSDT (Demo) was opened: 0.01 at 60000, with its stop-loss at 59000 and its take-profit at 62000.",
            opened.Content);

        // What settles its orders, and what reconciles it, hear of the same execution, and say nothing again.
        await application.SettleAsync(agent.Id);
        await application.ReconcileAsync(agent.Id);

        Assert.Single(await application.NotificationsOfAsync(agent.UserId, positions: true));

        // It is no critical event: no email is to be sent for it.
        Assert.False(opened.EmailPending);
    }

    [Fact]
    public async Task A_position_of_Futures_says_its_side()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        await application.PlaceAsync(agent, GuardCases.OpenShort);

        Assert.Equal(
            ["A short position in BTCUSDT (Demo) was opened: 0.02 at 60000, with its stop-loss at 61000 and its take-profit at 58000."],
            await SaidAsync(application, agent, NotificationType.PositionOpened));
    }

    [Fact]
    public async Task The_user_of_an_agent_is_told_once_that_a_position_was_closed_with_what_it_made()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price reaches the stop-loss at 59000, which sells the 0.01 there.
        application.Binance.Execute(application.Binance.The(stop: true));
        await application.SettleAsync(agent.Id);

        // It is closed, and nobody is told yet: what it made is known when its result is.
        Assert.Empty(await SaidAsync(application, agent, NotificationType.PositionClosed));

        await application.RecordResultsAsync();

        // 10 lost, with the fees of 0.6 and 0.59: 11.19, which is 1.119 times what it risked.
        var closed = Assert.Single(await application.NotificationsOfAsync(agent.UserId, positions: true), told => told.Type == NotificationType.PositionClosed);

        Assert.Equal((Guid?)agent.Id, closed.AgentId);
        Assert.Equal("The position in BTCUSDT (Demo) was closed by its stop-loss. It lost 11.19 USDT after fees, which is -1.12 R.", closed.Content);
        Assert.False(closed.EmailPending);

        // A position has one result, so it is said once.
        await application.RecordResultsAsync();

        Assert.Single(await SaidAsync(application, agent, NotificationType.PositionClosed));
    }

    [Fact]
    public async Task A_position_that_its_take_profit_closed_says_what_it_made()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price reaches the take-profit at 62000: 20 made, less the fees of 0.6 and 0.62.
        application.Binance.Execute(application.Binance.TheTakeProfit());
        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        Assert.Equal(
            ["The position in BTCUSDT (Demo) was closed by its take-profit. It made 18.78 USDT after fees, which is +1.88 R."],
            await SaidAsync(application, agent, NotificationType.PositionClosed));
    }

    [Fact]
    public async Task A_position_that_its_team_closed_says_so()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // A later cycle of the agent decides to close it, at 61000: 10 made, less 0.6 and 0.61.
        application.Binance.Price = 61000m;
        await application.PlaceAsync(agent, GuardCases.Close);
        await application.SettleAsync(agent.Id);
        await application.RecordResultsAsync();

        Assert.Equal(
            ["The position in BTCUSDT (Demo) was closed by the team of the agent. It made 8.79 USDT after fees, which is +0.88 R."],
            await SaidAsync(application, agent, NotificationType.PositionClosed));
    }

    [Fact]
    public async Task A_position_of_Futures_that_its_user_closed_says_its_side_and_counts_its_funding()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var opened = application.Clock.GetUtcNow();

        application.Binance.ExecutedAt = opened;
        await application.PlaceAsync(agent, GuardCases.OpenShort);
        await application.SettleAsync(agent.Id);

        // Three hours later its user closes it at 59000: 20 made, for fees of 1.2 and 1.18.
        application.Clock.Set(opened.AddHours(3));
        application.Binance.ExecutedAt = opened.AddHours(3);
        application.Binance.Price = 59000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);

        // It paid 0.12 as funding, and received 0.05, which is read some minutes after the close.
        application.Application.Providers.Funding = _ => new ExchangeFundings(
            ExchangeCallOutcome.Succeeded,
            [new ExchangeFunding("7001", -0.12m, "USDT", opened.AddHours(1)), new ExchangeFunding("7002", 0.05m, "USDT", opened.AddHours(2))],
            More: false);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(5));
        await application.WatchAsync();
        await application.RecordResultsAsync();

        Assert.Equal(
            ["The short position in BTCUSDT (Demo) was closed by you. It made 17.55 USDT after fees and funding, which is +0.88 R."],
            await SaidAsync(application, agent, NotificationType.PositionClosed));
    }

    [Fact]
    public async Task A_result_that_is_not_complete_says_so()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        // The executions of the order that closed it stay unread, until they are no longer
        // asked for: its result is what there is, and its user is told then.
        application.Clock.Set(position.ClosedAt!.Value.AddHours(25).AddSeconds(1));
        await application.RecordResultsAsync();

        Assert.Equal(
            [
                "The position in BTCUSDT (Demo) was closed by you. It made 9.4 USDT after fees, which is +0.94 R. " +
                "Not all of it could be read from Binance, so this is what is known of it.",
            ],
            await SaidAsync(application, agent, NotificationType.PositionClosed));
    }

    [Fact]
    public async Task A_position_that_was_closed_at_Binance_is_told_as_a_mismatch_and_then_as_closed()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var opened = application.Clock.GetUtcNow();

        application.Binance.ExecutedAt = opened;
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        application.Clock.Set(opened.AddHours(1));

        // Its user closes the position at Binance by hand, at 50000.
        application.Binance.Price = 50000m;
        application.Binance.HoldByHand(Market.Futures, 0m);
        Assert.True(await application.ReconcileAsync(agent.Id));
        await application.SettleAsync(agent.Id);

        // What did not match is said at once, and that it is closed when its result is recorded.
        Assert.Single(await SaidAsync(application, agent, NotificationType.ReconciliationMismatch));
        Assert.Empty(await SaidAsync(application, agent, NotificationType.PositionClosed));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        application.Clock.Set(position.ClosedAt!.Value.AddMinutes(5));
        await application.WatchAsync();
        await application.RecordResultsAsync();

        Assert.Equal(
            [
                "The long position in BTCUSDT (Demo) was closed at Binance, and by no order of the agent. It lost 201.2 USDT after " +
                "fees and funding, which is -10.06 R. Not all of it could be read from Binance, so this is what is known of it.",
            ],
            await SaidAsync(application, agent, NotificationType.PositionClosed));
    }

    /// <summary>What the user of an agent was told about it of one kind, in the order that it was said in.</summary>
    private static async Task<IReadOnlyList<string>> SaidAsync(OrdersApplication application, Agent agent, NotificationType type) =>
        [.. (await application.NotificationsOfAsync(agent.UserId, positions: true)).Where(told => told.Type == type && told.AgentId == agent.Id).Select(told => told.Content)];
}
