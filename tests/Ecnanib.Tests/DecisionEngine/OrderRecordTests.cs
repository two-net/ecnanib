using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The orders, the executions, and the positions of agents, with what the positions received and
/// paid as funding, as they are stored: whose they are, what an agent can have of them at a
/// time, and what they answer to those that ask what an agent holds (FR-AGT-8, FR-AGT-11,
/// FR-CYC-2, FR-CYC-5, NFR-DAT-9). See "The record of an order" in docs/orders.md.
/// </summary>
public class OrderRecordTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ProvesOwnershipOf("orders", "fills", "positions", "fundings")]
    public async Task The_orders_of_an_agent_are_of_the_user_of_the_agent_and_of_no_other()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var adas = await application.GiveAgentAsync(ada.Id, Morning);
        var graces = await application.GiveAgentAsync(grace.Id, Morning);

        var gracesPosition = await application.GiveOrdersAsync(grace.Id, graces, cycleId: null, Morning);
        var before = application.RowsOf(grace.Id);

        // The orders of Ada's agent are stored as Ada's, and change nothing of Grace's.
        var adasPosition = await application.GiveOrdersAsync(ada.Id, adas, cycleId: null, Morning);

        Assert.Equal(before, application.RowsOf(grace.Id));

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // Whoever asks for the rows of a user finds those of that user, and no others.
        Assert.Equal(
            [adasPosition],
            await context.Set<Position>().OwnedBy(ada.Id).Select(position => position.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [gracesPosition],
            await context.Set<Order>().OwnedBy(grace.Id).Select(order => order.PositionId!.Value).Distinct().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(
            await context.Set<Order>().OwnedBy(ada.Id).Where(order => order.AgentId == graces).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await context.Set<Fill>().OwnedBy(ada.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [adasPosition],
            await context.Set<Funding>().OwnedBy(ada.Id).Select(funding => funding.PositionId).ToListAsync(TestContext.Current.CancellationToken));

        // The owner of an order and of a position is the owner of its agent, the owner of an
        // execution the owner of its order, and the owner of what a position received and paid
        // as funding the owner of the position: the database holds to each, whatever wrote the row.
        var gracesOrder = await context.Set<Order>().AsNoTracking().FirstAsync(order => order.AgentId == graces, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(application, OrderOf(graces, ada.Id, OrderStatus.Filled)));
        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(application, PositionOf(graces, ada.Id, closed: true)));
        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(
            application, new Fill(Guid.CreateVersion7(), gracesOrder.Id, ada.Id, "9100", 100_000m, 0.01m, 0.5m, "USDT", Morning)));
        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(
            application, new Funding(Guid.CreateVersion7(), gracesPosition, ada.Id, "8100", -0.1m, "USDT", Morning)));

        // What a position received or paid at a funding time is stored once for it.
        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(
            application, new Funding(Guid.CreateVersion7(), adasPosition, ada.Id, "8001", -0.1m, "USDT", Morning)));

        Assert.Equal(before, application.RowsOf(grace.Id));
        Assert.Single(application.RowsOf(ada.Id)["positions"]);
    }

    [Fact]
    public async Task The_orders_of_an_agent_go_with_it_when_it_is_deleted()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var kept = await application.GiveAgentAsync(ada.Id, Morning, name: "Kept");
        var deleted = await application.GiveAgentAsync(ada.Id, Morning, name: "Deleted", symbol: "SOLUSDT");
        var keptPosition = await application.GiveOrdersAsync(ada.Id, kept, cycleId: null, Morning);
        await application.GiveOrdersAsync(ada.Id, deleted, cycleId: null, Morning);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await context.Set<Agent>().Where(agent => agent.Id == deleted).ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        // Its positions, with their funding, its orders, and their executions are gone, and
        // those of the other agent stay.
        Assert.Equal([keptPosition], await context.Set<Position>().Select(position => position.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await context.Set<Order>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await context.Set<Fill>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [keptPosition],
            await context.Set<Funding>().Select(funding => funding.PositionId).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_has_one_open_position_and_one_order_of_each_purpose_that_is_not_over_at_most()
    {
        using var application = new AccountsApplication(clock: new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.Id, Morning);

        // An agent holds at most one position (FR-CYC-5): the database holds to it.
        await StoreAsync(application, PositionOf(agent, ada.Id, closed: false));
        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(application, PositionOf(agent, ada.Id, closed: false)));
        await StoreAsync(application, PositionOf(agent, ada.Id, closed: true));

        // And to one order of each purpose that is not over: no second entry beside one that is
        // being sent, whose outcome is unknown, or that is with the exchange.
        foreach (var live in OrderStatuses.Live)
        {
            var first = OrderOf(agent, ada.Id, live);
            await StoreAsync(application, first);

            await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(application, OrderOf(agent, ada.Id, OrderStatus.Sending)));

            await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);
            await context.Set<Order>().Where(order => order.Id == first.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        // Orders that are over do not stand in the way, however many.
        await StoreAsync(application, OrderOf(agent, ada.Id, OrderStatus.Filled));
        await StoreAsync(application, OrderOf(agent, ada.Id, OrderStatus.NotPlaced));
        await StoreAsync(application, OrderOf(agent, ada.Id, OrderStatus.Sending));

        // No two orders carry the same identifier (FR-BIN-6).
        var one = OrderOf(agent, ada.Id, OrderStatus.Rejected);
        await StoreAsync(application, one);
        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(application, OrderOf(agent, ada.Id, OrderStatus.Rejected, one.ClientOrderId)));
    }

    [Fact]
    public async Task What_an_agent_holds_is_said_to_those_that_ask_from_its_position_and_its_orders()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var exposure = Assert.Single(application.Application.Exposures);
        var holdings = Assert.Single(application.Application.Holdings);

        // An agent that has placed nothing holds nothing, and its equity is its allocated capital.
        Assert.False(await HoldsAsync(exposure.HasOpenPositionOrOrdersAsync));
        Assert.False(await HoldsAsync(exposure.HasOpenPositionAsync));
        Assert.Equal(AgentHoldings.NothingYet(agent), await holdings.OfAsync(agent, TestContext.Current.CancellationToken), Same);

        await application.PlaceAsync(agent, GuardCases.OpenShort);

        // With a position: Agents is told so, and a cycle reads the position with the
        // stop-loss and the take-profit that rest for it.
        Assert.True(await HoldsAsync(exposure.HasOpenPositionOrOrdersAsync));
        Assert.True(await HoldsAsync(exposure.HasOpenPositionAsync));

        var holds = await holdings.OfAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal(new PositionContext(PositionSide.Short, 60000m, 0.02m, 61000m, TakeProfit: 58000m), holds.Position);
        Assert.Equal(
            [new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Buy, 0.02m, 61000m), new OpenOrder(OpenOrderKind.TakeProfit, OrderSide.Buy, 0.02m, 58000m)],
            holds.Orders);
        Assert.Equal((1000m, 1000m, 1000m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));

        // Closed: it holds nothing again, so its market and its symbol can be changed (FR-AGT-8).
        Assert.Equal(PositionCloseOutcome.Closed, await exposure.ClosePositionAtMarketAsync(agent.Id, TestContext.Current.CancellationToken));
        Assert.False(await HoldsAsync(exposure.HasOpenPositionOrOrdersAsync));
        Assert.False(await HoldsAsync(exposure.HasOpenPositionAsync));
        Assert.Null((await holdings.OfAsync(agent, TestContext.Current.CancellationToken)).Position);

        async Task<bool> HoldsAsync(Func<EcnanibDbContext, Guid, CancellationToken, Task<bool>> ask)
        {
            await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

            return await ask(context, agent.Id, TestContext.Current.CancellationToken);
        }

        static bool Same(AgentHoldings expected, AgentHoldings actual) =>
            (expected.Equity, expected.Position, expected.EquityAtDayStart, expected.PeakEquity)
                == (actual.Equity, actual.Position, actual.EquityAtDayStart, actual.PeakEquity)
            && expected.Orders.SequenceEqual(actual.Orders);
    }

    [Fact]
    public async Task An_agent_whose_entry_is_under_way_has_a_position_as_far_as_stopping_it_goes()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var exposure = Assert.Single(application.Application.Exposures);

        // The entry is with Binance, and its outcome is not known: the cycle was stopped as
        // the call ended without an answer.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = _ =>
        {
            stopping.Cancel();

            return OrderFate.TakenUnsaid;
        };

        await application.Orders.PlaceAsync(
            application.CycleOf(agent), Ecnanib.RiskGuard.GuardCheck.Of(GuardCases.OpenLong), stopping.Token);

        // It may have opened a position, so its user is asked what to do with one when they
        // stop the agent (FR-AGT-11), and its market cannot be changed under it (FR-AGT-8).
        await using (var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await exposure.HasOpenPositionAsync(context, agent.Id, TestContext.Current.CancellationToken));
            Assert.True(await exposure.HasOpenPositionOrOrdersAsync(context, agent.Id, TestContext.Current.CancellationToken));
        }

        // A close that its user asks for settles the entry first: it was executed, so there
        // is a position, which is closed. The stop-loss is placed and taken off on the way.
        application.Binance.Fate = null;
        application.Clock.Set(application.Clock.GetUtcNow().AddSeconds(30));

        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        Assert.Equal(ExitReason.UserClose, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.Equal(1, application.Binance.Asked.Count(asked => asked.Side == "BUY"));
    }

    private static async Task StoreAsync(AccountsApplication application, object row)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.Add(row);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Order OrderOf(Guid agentId, Guid userId, OrderStatus status, string? clientOrderId = null)
    {
        var id = Guid.CreateVersion7();

        return new Order(
            id, agentId, userId, TradingMode.Demo, Market.Spot, "BTCUSDT", cycleId: null, positionId: null,
            clientOrderId ?? Order.ClientOrderIdOf(id), exchangeOrderId: null, OrderPurpose.Entry, OrderType.Market, OrderSide.Buy,
            price: null, stopLoss: 59000m, takeProfit: 62000m, quantity: 0.01m, status, executedQuantity: 0m, averagePrice: null,
            exitReason: null, sends: 1, fillsRecorded: false, Morning, Morning, checkedAt: null, pairId: null, cancelAfter: null);
    }

    private static Position PositionOf(Guid agentId, Guid userId, bool closed) => new(
        Guid.CreateVersion7(), agentId, userId, TradingMode.Demo, Market.Spot, "BTCUSDT", PositionSide.Long, 60000m, 0.01m, 59000m, 62000m,
        Morning, closed ? Morning.AddHours(1) : null, closed ? ExitReason.StopLoss : null, alertedAt: null, fundingDueAt: null);
}
