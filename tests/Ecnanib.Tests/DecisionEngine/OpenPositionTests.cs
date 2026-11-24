using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Experience;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Positions update in the UI without a manual refresh (FR-NTF-2): the page of an agent says the
/// open position of the agent, which is asked for by owner (NFR-SEC-3), with the protection
/// that rests on the exchange for it. What a position is worth at a price is one rule, which
/// the equity of an agent is worked out with as well. See "The position on a page" in
/// docs/orders.md.
/// </summary>
public class OpenPositionTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_open_position_of_an_agent_is_said_to_its_user_and_an_agent_that_holds_none_says_none()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var holds = await application.GiveAgentAsync(ada.Id, Morning, name: "Holds");
        var held = await application.GiveAgentAsync(ada.Id, Morning, name: "Held");
        var never = await application.GiveAgentAsync(ada.Id, Morning, name: "Never");

        // One agent holds a position, after one that is over. Another held one, which is over.
        await application.GiveTradeAsync(ada.Id, holds, Morning.AddHours(-3));
        await application.GiveTradeAsync(ada.Id, holds, Morning, holdingSeconds: 600, closed: false);
        await application.GiveTradeAsync(ada.Id, held, Morning.AddHours(-1));

        var position = await application.OpenPositions.OfAgentAsync(ada.Id, holds, TestContext.Current.CancellationToken);

        // The position as it is stored: nothing of the exchange is asked for it.
        Assert.Equal(
            new OpenPosition(
                Market.Spot,
                TradingMode.Demo,
                "BTCUSDT",
                "USDT",
                PositionSide.Long,
                Quantity: 0.01m,
                EntryPrice: 100_000m,
                StopLoss: null,
                TakeProfit: null,
                OpenedAt: Morning.AddMinutes(-10)),
            position);

        Assert.Null(await application.OpenPositions.OfAgentAsync(ada.Id, held, TestContext.Current.CancellationToken));
        Assert.Null(await application.OpenPositions.OfAgentAsync(ada.Id, never, TestContext.Current.CancellationToken));
        Assert.Null(await application.OpenPositions.OfAgentAsync(ada.Id, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_position_of_an_agent_of_another_user_is_not_found()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var ofAda = await application.GiveAgentAsync(ada.Id, Morning);
        var ofGrace = await application.GiveAgentAsync(grace.Id, Morning);

        await application.GiveTradeAsync(ada.Id, ofAda, Morning, closed: false);

        // The id of an agent says which position, and never whose: to Grace, the agent of Ada
        // holds nothing, as an agent that does not exist holds nothing.
        Assert.NotNull(await application.OpenPositions.OfAgentAsync(ada.Id, ofAda, TestContext.Current.CancellationToken));
        Assert.Null(await application.OpenPositions.OfAgentAsync(grace.Id, ofAda, TestContext.Current.CancellationToken));
        Assert.Null(await application.OpenPositions.OfAgentAsync(grace.Id, ofGrace, TestContext.Current.CancellationToken));
        Assert.Null(await application.OpenPositions.OfAgentAsync(ada.Id, ofGrace, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_symbols_that_the_agents_of_a_user_hold_are_said_each_once_and_to_that_user_alone()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Ada has nothing open yet.
        Assert.Empty(await application.OpenPositions.HeldByUserAsync(ada.Id, TestContext.Current.CancellationToken));

        var spot = await application.GiveAgentAsync(ada.Id, Morning, name: "Spot");
        var futures = await application.GiveAgentAsync(ada.Id, Morning, name: "Futures", market: Market.Futures);
        var live = await application.GiveAgentAsync(ada.Id, Morning, name: "Live", mode: TradingMode.Live);
        var ether = await application.GiveAgentAsync(ada.Id, Morning, name: "Ether", symbol: "ETHUSDT", state: AgentState.Stopped);
        var again = await application.GiveAgentAsync(ada.Id, Morning, name: "Again", symbol: "ETHUSDT", state: AgentState.Stopped);
        var held = await application.GiveAgentAsync(ada.Id, Morning, name: "Held", symbol: "SOLUSDT");
        var hers = await application.GiveAgentAsync(grace.Id, Morning, name: "Hers", symbol: "BNBUSDT");

        foreach (var agent in new[] { spot, futures, live, ether, again })
        {
            await application.GiveTradeAsync(ada.Id, agent, Morning, closed: false);
        }

        // A position that is over is held no longer, and one of another user is not Ada's.
        await application.GiveTradeAsync(ada.Id, held, Morning.AddHours(-1));
        await application.GiveTradeAsync(grace.Id, hers, Morning, closed: false);

        // Each symbol once, on its market, in its mode: two agents that hold the same are one
        // price to follow. Nothing else of a position is said.
        Assert.Equal(
            [
                new HeldSymbol(Market.Spot, TradingMode.Demo, "BTCUSDT"),
                new HeldSymbol(Market.Spot, TradingMode.Demo, "ETHUSDT"),
                new HeldSymbol(Market.Futures, TradingMode.Demo, "BTCUSDT"),
                new HeldSymbol(Market.Spot, TradingMode.Live, "BTCUSDT"),
            ],
            await application.OpenPositions.HeldByUserAsync(ada.Id, TestContext.Current.CancellationToken));

        Assert.Equal(
            [new HeldSymbol(Market.Spot, TradingMode.Demo, "BNBUSDT")],
            await application.OpenPositions.HeldByUserAsync(grace.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_protection_of_a_position_is_said_while_an_order_for_it_rests_on_the_exchange_and_not_otherwise()
    {
        using var application = new AccountsApplication();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning);
        var position = await application.GiveTradeAsync(ada.Id, agent, Morning, closed: false);

        // The stop-loss rests, and the take-profit is still on its way: the price alone protects nothing.
        var stopLoss = await application.GiveProtectionAsync(ada.Id, agent, position, OrderPurpose.StopLoss, price: 99_000m, Morning);
        var takeProfit = await application.GiveProtectionAsync(ada.Id, agent, position, OrderPurpose.TakeProfit, price: 102_000m, Morning, OrderStatus.Sending);

        var held = await application.OpenPositions.OfAgentAsync(ada.Id, agent, TestContext.Current.CancellationToken);
        Assert.Equal((99_000m, (decimal?)null), (held!.StopLoss, held.TakeProfit));

        // Both rest, one of them executed in part.
        await MoveAsync(application, takeProfit, OrderStatus.PartiallyFilled);

        held = await application.OpenPositions.OfAgentAsync(ada.Id, agent, TestContext.Current.CancellationToken);
        Assert.Equal((99_000m, 102_000m), (held!.StopLoss, held.TakeProfit));

        // An order that the exchange took off no longer rests.
        await MoveAsync(application, stopLoss, OrderStatus.Canceled);

        held = await application.OpenPositions.OfAgentAsync(ada.Id, agent, TestContext.Current.CancellationToken);
        Assert.Equal(((decimal?)null, 102_000m), (held!.StopLoss, held.TakeProfit));
    }

    [Theory]
    [InlineData(PositionSide.Long, 100_000, 0.02, 100_500, 10)]
    [InlineData(PositionSide.Long, 100_000, 0.02, 99_000, -20)]
    [InlineData(PositionSide.Short, 100_000, 0.02, 99_000, 20)]
    [InlineData(PositionSide.Short, 100_000, 0.02, 100_500, -10)]
    [InlineData(PositionSide.Long, 100_000, 0.02, 100_000, 0)]
    public void What_a_position_would_make_at_a_price_is_how_far_the_price_is_from_its_entry_in_its_direction_times_what_it_holds(
        PositionSide side, decimal entryPrice, decimal quantity, decimal price, decimal made)
    {
        Assert.Equal(made, PositionValues.Unrealized(side, entryPrice, quantity, price));

        // A page says it of the position that it shows, by the same rule.
        var position = new OpenPosition(
            Market.Futures, TradingMode.Demo, "BTCUSDT", "USDT", side, quantity, entryPrice, StopLoss: null, TakeProfit: null, Morning);

        Assert.Equal(made, position.UnrealizedAt(price));
    }

    [Fact]
    public void A_position_is_valued_at_the_mark_price_on_futures_and_at_the_price_that_its_symbol_last_traded_at_on_spot()
    {
        Assert.Equal(ExchangePriceKind.Mark, PositionValues.PriceOf(Market.Futures));
        Assert.Equal(ExchangePriceKind.LastTraded, PositionValues.PriceOf(Market.Spot));
    }

    private static async Task MoveAsync(AccountsApplication application, Guid orderId, OrderStatus status)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        await context.Set<Order>()
            .Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(row => row.SetProperty(order => order.Status, status), TestContext.Current.CancellationToken);
    }
}
