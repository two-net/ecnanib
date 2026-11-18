using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Every order carries a unique client order identifier. If the outcome of a request is unknown,
/// the system queries the order's status before any retry, so an order is never sent twice
/// (FR-BIN-6). A restart never creates a duplicate order (NFR-REL-3). See "An order is never sent
/// twice" in docs/orders.md.
/// </summary>
public class OrderIdempotencyTests
{
    [Fact]
    public async Task Every_order_carries_an_identifier_of_its_own_that_is_stored_before_the_order_is_sent()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // What the records hold of an order at the moment that Binance is sent it. The pair of
        // a stop-loss and a take-profit is two orders that are sent with one call.
        var storedAsSent = new List<(string ClientOrderId, OrderStatus? Status)>();
        application.Binance.Fate = asked =>
        {
            var stored = application.OrdersOfAsync(agent.Id).GetAwaiter().GetResult();

            foreach (var identifier in IdentifiersOf(asked))
            {
                storedAsSent.Add((identifier, stored.SingleOrDefault(order => order.ClientOrderId == identifier)?.Status));
            }

            // The pair is refused, so that one more order follows: the one that closes.
            return asked.Stop ? OrderFate.Refused : OrderFate.Taken;
        };

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Four orders were sent, with three calls, and the row of each was there first, saying
        // that it is being sent.
        Assert.Equal(3, application.Binance.Asked.Count);
        Assert.Equal(4, storedAsSent.Count);
        Assert.All(storedAsSent, sent => Assert.Equal(OrderStatus.Sending, sent.Status));

        // Each carries the identifier of its row, which is the id of the row as 32 hexadecimal
        // digits, and no two carry the same one.
        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(orders.Select(order => order.ClientOrderId), application.Binance.Asked.SelectMany(IdentifiersOf));
        Assert.All(orders, order => Assert.Equal(order.Id.ToString("N"), order.ClientOrderId));
        Assert.Equal(4, orders.Select(order => order.ClientOrderId).Distinct().Count());
        Assert.All(orders, order => Assert.Matches("^[0-9a-f]{32}$", order.ClientOrderId));

        // The pair carries one more, which both of its rows say, and which no order carries.
        var pair = Assert.Single(application.Binance.Asked, asked => asked.PairId is not null);
        Assert.Equal([pair.PairId, pair.PairId], orders.Where(order => order.PairId is not null).Select(order => Order.PairIdOf(order.PairId!.Value)));
        Assert.DoesNotContain(pair.PairId, orders.Select(order => order.ClientOrderId));

        static IEnumerable<string> IdentifiersOf(AskedOrder asked) =>
            asked.TakeProfitId is { } takeProfit ? [asked.ClientOrderId, takeProfit] : [asked.ClientOrderId];
    }

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task An_entry_that_Binance_took_before_the_call_timed_out_is_found_and_protected_and_no_second_order_is_placed(Market market)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: market);
        var quantity = market == Market.Spot ? 0.01m : 0.02m;

        // Binance takes the entry, and the answer never comes.
        application.Binance.Fate = asked => asked.Stop ? OrderFate.Taken : OrderFate.TakenUnsaid;

        await application.PlaceAsync(agent, market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures);

        // The entry was sent once. It was asked about by its identifier, found executed, and
        // protected: the one other order that was sent is its stop-loss.
        var entry = application.Binance.The(stop: false);
        Assert.Equal(
            [(false, "BUY", quantity), (true, "SELL", quantity)],
            application.Binance.Asked.Select(asked => (asked.Stop, asked.Side, asked.Quantity)));
        Assert.Contains(entry.Asked.ClientOrderId, application.Binance.AskedAbout);

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((quantity, null), (position.Quantity, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, 1),
                (OrderPurpose.StopLoss, OrderStatus.Open, 1),
                .. market == Market.Spot ? new[] { (OrderPurpose.TakeProfit, OrderStatus.Open, 1) } : [],
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Sends)));
    }

    [Fact]
    public async Task An_entry_that_Binance_does_not_have_once_it_could_no_longer_arrive_was_not_placed_and_is_never_sent_again()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var settled = Assert.Single(application.Application.Reconciliations);

        // The call that places the entry ends with no answer, and Binance has no such order.
        // The cycle is stopped at that moment, so it does not wait to ask.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = _ =>
        {
            stopping.Cancel();

            return OrderFate.Lost;
        };

        await application.Orders.PlaceAsync(
            application.CycleOf(agent), GuardCheck.Of(GuardCases.OpenLong), stopping.Token);

        var unknown = Assert.Single(await application.OrdersOfAsync(agent.Id));
        Assert.Equal(OrderStatus.Unknown, unknown.Status);

        // Whether Binance has the order is not known, so the agent gets no cycle: it would
        // decide on records that may be wrong, and could place a second entry.
        Assert.False(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // Asked at once, Binance says that it has no such order. That says nothing yet: the
        // order could still arrive.
        await application.Orders.SettleAsync(agent.Id, TestContext.Current.CancellationToken);

        Assert.Equal([unknown.ClientOrderId], application.Binance.AskedAbout);
        Assert.Equal(OrderStatus.Unknown, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);
        Assert.False(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // Once it can no longer arrive, the same answer is final: the entry was not placed.
        application.Clock.Set(application.Clock.GetUtcNow() + ExchangeOrderTimes.FinalAfter);
        await application.Orders.SettleAsync(agent.Id, TestContext.Current.CancellationToken);

        Assert.Equal(OrderStatus.NotPlaced, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);
        Assert.True(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // It was sent once, and never again: no failure leads to an entry (NFR-REL-2).
        Assert.Single(application.Binance.Asked);
        Assert.Empty(application.Binance.Orders);
        Assert.Empty(await application.PositionsOfAsync(agent.Id));
    }

    [Fact]
    public async Task A_stop_loss_that_Binance_took_before_the_call_timed_out_is_found_resting_and_is_not_sent_again()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        application.Binance.Fate = asked => asked.Stop ? OrderFate.TakenUnsaid : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // One entry, and one stop-loss, which was asked about and found resting.
        var stopLoss = application.Binance.The(stop: true);
        Assert.Equal([false, true], application.Binance.Asked.Select(asked => asked.Stop));
        Assert.Equal([stopLoss.Asked.ClientOrderId], application.Binance.AskedAbout);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled, 1), (OrderPurpose.StopLoss, OrderStatus.Open, 1)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Sends)));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_stop_loss_that_Binance_does_not_have_is_asked_about_before_it_is_sent_once_more_as_the_same_order()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The first call that places the stop-loss is lost. The second one is taken.
        var stops = 0;
        application.Binance.Fate = asked => asked.Stop && ++stops == 1 ? OrderFate.Lost : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The stop-loss was sent, asked about, and only then sent again: with the identifier
        // that it had, so that Binance would refuse it if it had the first one after all. The
        // same holds for the pair of the Spot market: see ProtectivePairTests.
        var stopLoss = Assert.Single(await application.OrdersOfAsync(agent.Id), order => order.Purpose == OrderPurpose.StopLoss);
        Assert.Equal(
            ["POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "GET /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder"],
            application.Binance.OrderCalls);
        Assert.Equal([stopLoss.ClientOrderId], application.Binance.AskedAbout);
        Assert.Equal(
            [stopLoss.ClientOrderId, stopLoss.ClientOrderId],
            application.Binance.Asked.Where(asked => asked.Stop).Select(asked => asked.ClientOrderId));

        // It rests, as the one stop-loss of the position, and nobody was told anything.
        Assert.Equal((OrderStatus.Open, 2), (stopLoss.Status, stopLoss.Sends));
        Assert.Equal("NEW", application.Binance.The(stop: true).Status);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_restart_asks_about_the_order_that_was_under_way_and_places_no_duplicate()
    {
        var database = PostgresServer.CreateDatabase();
        var binance = new TradingBinance();
        Agent agent;

        using (var before = new OrdersApplication(database: database, binance: binance))
        {
            agent = await before.GiveAgentAsync();

            // The application wrote the row of an entry, sent the order, and was stopped before
            // the answer came: Binance has the order, and the row still says that it is being sent.
            var id = Guid.CreateVersion7();

            await using (var context = await before.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
            {
                context.Add(new Order(
                    id, agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, cycleId: Guid.CreateVersion7(), positionId: null,
                    Order.ClientOrderIdOf(id), exchangeOrderId: null, OrderPurpose.Entry, OrderType.Market, OrderSide.Buy, price: null,
                    stopLoss: 59000m, takeProfit: 62000m, quantity: 0.01m, OrderStatus.Sending, executedQuantity: 0m, averagePrice: null,
                    exitReason: null, sends: 1, fillsRecorded: false, OrdersApplication.Start, OrdersApplication.Start, checkedAt: null,
                    pairId: null, cancelAfter: null));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var owner = agent.UserId.ToString("N");
            await before.Application.ExchangeOrders.PlaceAsync(
                agent.UserId, agent.Market, agent.Mode, Keys.Of(owner, "demo"), Keys.Of(owner, "seal"),
                new ExchangeOrderRequest(
                    Order.ClientOrderIdOf(id), agent.Symbol, ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.01m, Price: null, Closes: false),
                TestContext.Current.CancellationToken);
        }

        // The application starts again, a minute later, on the same database and the same
        // Binance, and runs its background work as it does outside the tests.
        using var after = new OrdersApplication(settlesOrders: true, database: database, binance: binance);
        after.Clock.Set(OrdersApplication.Start.AddMinutes(1));
        var settled = Assert.Single(after.Application.Reconciliations);

        // It settles first: the job that settles the orders is due as the work starts, asks
        // Binance about the entry by its identifier, finds it executed, opens the position in
        // the records, and protects it. Until then the agent gets no cycle, and so places no order.
        var began = System.Diagnostics.Stopwatch.GetTimestamp();

        while (!await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken))
        {
            Assert.True(
                System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(30),
                "The application did not settle the order that was under way.");

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        var entry = (await after.OrdersOfAsync(agent.Id))[0];
        Assert.Contains(entry.ClientOrderId, binance.AskedAbout);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await after.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Null(Assert.Single(await after.PositionsOfAsync(agent.Id)).ClosedAt);

        // The entry reached Binance once, before the restart, and nothing sent it again: what
        // was placed after it is the pair of its stop-loss and its take-profit, with one call.
        Assert.Equal([(false, "BUY"), (true, "SELL")], binance.Asked.Select(asked => (asked.Stop, asked.Side)));
        Assert.Single(binance.Orders, order => !order.Asked.Stop);
    }
}
