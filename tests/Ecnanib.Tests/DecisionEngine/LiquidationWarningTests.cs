using System.Globalization;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The system checks the margin state of open positions at each reconciliation and alerts the
/// user when a position approaches liquidation. It never adds margin on its own (FR-BIN-F5). A
/// position approaches liquidation when no more than a share of the way from its entry to the
/// price that the exchange liquidates it at is left, which the configuration file says: a
/// quarter, in the file of the tests. See "The margin of a position" in docs/orders.md.
/// </summary>
public class LiquidationWarningTests
{
    private const string Area = "Ecnanib.DecisionEngine.PositionWatch";

    private const string WhatToDo =
        "No margin is added to a position for you: close the position or reduce it, or add margin to it at Binance. " +
        "This is said once in an hour for an agent.";

    [Fact]
    public async Task The_margin_of_every_open_position_of_the_Futures_market_is_checked_with_every_run()
    {
        using var application = new OrdersApplication();
        var providers = application.Application.Providers;
        var (ada, _) = await OpenAsync(application, GuardCases.OpenLongOnFutures, "ada@example.com");
        var grace = await application.GiveAgentAsync("grace@example.com");
        await application.PlaceAsync(grace, GuardCases.OpenLong);
        providers.FuturesPosition = _ => HeldPositions.Long(mark: 60000m);

        // The position of the Futures market is asked about, in the account of its user, and
        // the one of the Spot market, which has no margin, is not.
        await application.WatchAsync();
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        await application.WatchAsync();

        Assert.Equal(
            [new AskedPosition(ada.UserId, TradingMode.Demo, "BTCUSDT"), new AskedPosition(ada.UserId, TradingMode.Demo, "BTCUSDT")],
            providers.FuturesPositionsAsked);

        // A position far from the price that it is liquidated at is one that nobody is told about.
        Assert.Empty(await application.NotificationsOfAsync(ada.UserId));
        Assert.DoesNotContain(application.Application.Events(), said => said.Area == Area && said.Level != "Debug");

        // One that is closed has no margin to check. What closes it for its user asks once what
        // the account holds, before it sends an order.
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(ada.Id));
        Assert.Equal(3, providers.FuturesPositionsAsked.Count);

        await application.WatchAsync();

        Assert.Equal(3, providers.FuturesPositionsAsked.Count);
    }

    [Theory]
    [InlineData("long", "65000", "50000", false)]
    [InlineData("long", "52500.01", "50000", false)]
    [InlineData("long", "52500", "50000", true)]
    [InlineData("long", "50000", "50000", true)]
    [InlineData("long", "49000", "50000", true)]
    [InlineData("long", "65000", "60000", true)]
    [InlineData("long", "65000", "61000", true)]
    [InlineData("short", "55000", "70000", false)]
    [InlineData("short", "67499.99", "70000", false)]
    [InlineData("short", "67500", "70000", true)]
    [InlineData("short", "70000", "70000", true)]
    [InlineData("short", "71000", "70000", true)]
    [InlineData("short", "55000", "60000", true)]
    [InlineData("short", "55000", "59000", true)]
    public async Task A_position_approaches_liquidation_when_a_quarter_of_the_way_from_its_entry_to_its_liquidation_price_is_left_or_less(
        string side, string markPrice, string liquidationPrice, bool approaches)
    {
        // Entered at 60000. The way of the long position is to 50000, and of the short one to
        // 70000: a quarter of it is 2500. A mark price that has reached the liquidation price
        // has none of the way left, and a liquidation price that is not on the losing side of
        // the entry leaves no way at all.
        var mark = decimal.Parse(markPrice, CultureInfo.InvariantCulture);
        var liquidation = decimal.Parse(liquidationPrice, CultureInfo.InvariantCulture);

        using var application = new OrdersApplication();
        var (agent, _) = await OpenAsync(application, side == "long" ? GuardCases.OpenLongOnFutures : GuardCases.OpenShort);
        application.Application.Providers.FuturesPosition = _ =>
            side == "long" ? HeldPositions.Long(mark, liquidation) : HeldPositions.Short(mark, liquidation);

        await application.WatchAsync();

        var notifications = await application.NotificationsOfAsync(agent.UserId);
        Assert.Equal(approaches ? [NotificationType.LiquidationWarning] : [], notifications.Select(notification => notification.Type));
        Assert.Equal(approaches ? 1 : 0, application.Application.Events().Count(said => said.Area == Area && said.Level == "Warning"));
    }

    [Fact]
    public async Task The_user_of_a_position_that_approaches_liquidation_is_told_with_the_prices_and_what_to_do()
    {
        using var application = new OrdersApplication();
        var (agent, _) = await OpenAsync(application, GuardCases.OpenLongOnFutures);
        application.Application.Providers.FuturesPosition = _ => HeldPositions.Long(mark: 52500m);

        await application.WatchAsync();

        // About the agent, in plain words: the two prices, how far they are apart, that the
        // service adds no margin, and what its user can do.
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.LiquidationWarning, agent.Id, false), (notification.Type, notification.AgentId, notification.IsRead));
        Assert.Equal(
            "The long position in BTCUSDT (Demo) approaches liquidation: its mark price is 52500, and Binance liquidates it at 50000, " +
            "which is 4.8% away. " + WhatToDo,
            notification.Content);

        // It is said in the log as well, with how much of the way is left, and with no price
        // and no amount, as the log of the orders has none.
        var said = Assert.Single(application.Application.Events(), written => written.Area == Area && written.Level == "Warning");
        Assert.Equal(
            "The position of agent {AgentId} in {Symbol} approaches liquidation: {WayLeftPercent} percent of the way from its entry " +
            "to its liquidation price is left",
            said.Template);
        Assert.Equal((agent.Id.ToString(), "BTCUSDT", 25m), (said.Text("AgentId"), said.Text("Symbol"), said["WayLeftPercent"].GetDecimal()));
        Assert.DoesNotContain("52500", said.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("50000", said.ToString(), StringComparison.Ordinal);

        // The agent goes on as it was: its team decides what becomes of the position.
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
    }

    [Theory]
    [InlineData("short", "67500", "its mark price is 67500, and Binance liquidates it at 70000, which is 3.7% away")]
    [InlineData("short", "70200", "its mark price is 70200, and has reached the price that Binance liquidates it at, 70000")]
    [InlineData("long", "49999.5", "its mark price is 49999.5, and has reached the price that Binance liquidates it at, 50000")]
    public async Task The_notification_says_the_side_of_the_position_and_a_mark_price_that_has_reached_the_liquidation_price(
        string side, string markPrice, string where)
    {
        var mark = decimal.Parse(markPrice, CultureInfo.InvariantCulture);

        using var application = new OrdersApplication();
        var (agent, _) = await OpenAsync(application, side == "long" ? GuardCases.OpenLongOnFutures : GuardCases.OpenShort);
        application.Application.Providers.FuturesPosition = _ => side == "long" ? HeldPositions.Long(mark) : HeldPositions.Short(mark);

        await application.WatchAsync();

        Assert.Equal(
            $"The {side} position in BTCUSDT (Demo) approaches liquidation: {where}. " + WhatToDo,
            Assert.Single(await application.NotificationsOfAsync(agent.UserId)).Content);
    }

    [Fact]
    public async Task The_user_is_told_once_in_an_hour_while_the_position_approaches_liquidation()
    {
        using var application = new OrdersApplication();
        var (agent, _) = await OpenAsync(application, GuardCases.OpenLongOnFutures);
        application.Application.Providers.FuturesPosition = _ => HeldPositions.Long(mark: 51000m);
        var first = application.Clock.GetUtcNow();

        // The margin is checked every few minutes, and the position stays where it is.
        foreach (var minutes in new[] { 0, 4, 8, 56 })
        {
            application.Clock.Set(first.AddMinutes(minutes));
            await application.WatchAsync();
        }

        application.Clock.Set(first.AddHours(1).AddSeconds(-1));
        await application.WatchAsync();

        // Every check finds it, and says so in the log. Its user is told once.
        Assert.Equal(5, application.Application.Providers.FuturesPositionsAsked.Count);
        Assert.Equal(5, application.Application.Events().Count(said => said.Area == Area && said.Level == "Warning"));
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));

        // And once more when an hour has passed.
        application.Clock.Set(first.AddHours(1));
        await application.WatchAsync();

        Assert.Equal(
            [first, first.AddHours(1)],
            (await application.NotificationsOfAsync(agent.UserId)).Select(notification => notification.CreatedAt));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("nothing")]
    [InlineData("other side")]
    [InlineData("unanswered")]
    public async Task Nobody_is_warned_about_a_position_whose_liquidation_price_the_exchange_does_not_say(string answer)
    {
        using var application = new OrdersApplication();
        var (agent, _) = await OpenAsync(application, GuardCases.OpenLongOnFutures);

        // The exchange names no liquidation price, which it does not for a position that it
        // cannot liquidate. The account holds nothing in the symbol, or a position on the other
        // side, where the records say a long one: nothing is corrected here, that is left to
        // reconciliation. Or the exchange does not answer.
        application.Application.Providers.FuturesPosition = _ => answer switch
        {
            "none" => HeldPositions.Long(mark: 52000m, liquidation: null),
            "nothing" => StandInProviders.NoPosition,
            "other side" => HeldPositions.Short(mark: 69000m),
            _ => ExchangeFuturesPosition.Without(ExchangeCallOutcome.TimedOut),
        };

        await application.WatchAsync();

        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));

        // What could not be read is a warning. A position that the account does not hold as
        // the records say is said as information: most often its stop-loss closed it a moment
        // ago, which what settles the orders sees with its next run.
        var said = application.Application.Events()
            .Where(written => written.Area == Area && written.Level is "Warning" or "Information")
            .Select(written => (written.Level, written.Template))
            .ToList();
        Assert.Equal(
            answer switch
            {
                "none" => [],
                "unanswered" => [("Warning", "The margin of the position of agent {AgentId} in {Symbol} could not be read: {ExchangeCallOutcome}")],
                _ =>
                [
                    ("Information",
                        "The account of agent {AgentId} does not hold the {PositionSide} position in {Symbol} that the records say, as the " +
                        "exchange says it: its margin is not checked, and its funding is not read"),
                ],
            },
            said);

        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        application.Application.Providers.FuturesPosition = _ => HeldPositions.Long(mark: 52000m);
        await application.WatchAsync();

        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task No_margin_is_added_to_a_position_that_approaches_liquidation_and_nothing_else_is_changed_at_the_exchange()
    {
        using var application = new OrdersApplication();
        var (agent, position) = await OpenAsync(application, GuardCases.OpenLongOnFutures);
        application.Application.Providers.FuturesPosition = _ => HeldPositions.Long(mark: 50100m);

        var changes = application.Binance.Changes.ToList();
        var calls = application.Binance.System.Calls.Count;
        var orders = (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Quantity)).ToList();

        await application.WatchAsync();
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));
        await application.WatchAsync();

        // Its user is told, and that is all: no call set a margin or placed an order, and no
        // call but the reads of the margin was made at all. The service has no call that adds
        // margin to a position: see NoFundMovementTests.
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(changes, application.Binance.Changes);
        Assert.Equal(calls, application.Binance.System.Calls.Count);
        Assert.Equal(2, application.Application.Providers.FuturesPositionsAsked.Count);

        // The position and its protection are as they were, and the agent runs on.
        Assert.Equal(orders, (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Quantity)));
        var now = Assert.Single(await application.PositionsOfAsync(agent.Id));

        Assert.Equal(
            (position.Quantity, position.StopLoss, position.TakeProfit, (DateTimeOffset?)null),
            (now.Quantity, now.StopLoss, now.TakeProfit, now.ClosedAt));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
    }

    /// <summary>Gives a new user an agent of the Futures market that opens a position, and gives both as they are stored.</summary>
    private static async Task<(Agent Agent, Position Position)> OpenAsync(
        OrdersApplication application, GuardCase plan, string email = "ada@example.com")
    {
        var agent = await application.GiveAgentAsync(email, Market.Futures);
        await application.PlaceAsync(agent, plan);

        return (agent, Assert.Single(await application.PositionsOfAsync(agent.Id)));
    }
}
