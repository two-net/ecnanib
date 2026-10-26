using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Services;
using Ecnanib.Execution.Services.Live;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>
/// NFR-REL-02 and NFR-REL-03 on the live venue with the exchange faked by the scripted account book: an answer that was lost
/// is resolved by client id before anything is retried, a retry places only what is missing, no protective order goes
/// before its replacement where the venue allows it, and the kill switch closes while the stop still stands.
/// </summary>
[Trait("Category", "Live")]
public class LiveReliabilityLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public LiveReliabilityLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    private static EntryPlacement Market(Guid decisionId, decimal quantity = 0.1m, decimal stop = 95m, decimal fraction = 0.33m) =>
        new(decisionId, PositionSide.Long, OrderType.Market, null, quantity, stop, [110m, 130m], fraction, null);

    private static ExchangeUnavailableException Timeout() => new("Binance place order failed: request timed out", ExchangeErrorKind.Timeout);

    private static ExchangeUnavailableException NetworkDown() => new("Binance query failed: connection reset", ExchangeErrorKind.Network);

    [Fact]
    public async Task AnEntryAcceptedThenTimedOutIsReadBackByClientIdAndAdoptedWithoutASecondOrder()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-entry-lost@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var lost = 0;
        account.AfterPlaceOrder = _ => Interlocked.Increment(ref lost) == 1 ? Timeout() : null;
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);

        var entry = await venue.PlaceEntryAsync(agent, Market(decision.Id), Ct);

        Assert.Single(account.PlacedOrders);
        Assert.Equal((OrderStatus.Filled, 0.1m), (entry.Status, entry.FilledQuantity));
        Assert.Equal(account.Orders.Single(o => o.ClientOrderId == entry.ClientOrderId).OrderId, entry.ExchangeOrderId);
        Assert.NotNull(await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id));
        Assert.Equal(2, account.PlacedOcos.Count);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("was lost; the exchange has it and it is adopted"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AFuturesStopAcceptedThenTimedOutIsAdoptedAndNeitherRetriedNorFlattened()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-stop-lost@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var lost = 0;
        account.AfterPlaceConditional = o => o.Type == OrderType.StopMarket && Interlocked.Increment(ref lost) == 1 ? Timeout() : null;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);

        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);

        Assert.Single(account.PlacedConditional, c => c.Type == OrderType.StopMarket);
        Assert.Equal(3, account.PlacedConditional.Count);
        var orders = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        var stop = Assert.Single(orders, o => o.Purpose == OrderPurpose.StopLoss);
        Assert.Equal(OrderStatus.New, stop.Status);
        Assert.NotNull(stop.ExchangeAlgoId);
        Assert.DoesNotContain(orders, o => o.Purpose == OrderPurpose.Close);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("conditional order") && e.Message.Contains("the exchange has it and it is adopted"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ASpotOcoAcceptedThenTimedOutIsAdoptedAndNoFlattenFollows()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-oco-lost@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var lost = 0;
        account.AfterPlaceOco = _ => Interlocked.Increment(ref lost) == 1 ? Timeout() : null;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);

        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);

        Assert.Equal(2, account.PlacedOcos.Count);
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        Assert.Equal(4, open.Count);
        Assert.All(open, o => Assert.NotNull(o.OrderListId));
        Assert.DoesNotContain(await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id), o => o.Purpose == OrderPurpose.Close);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("OCO") && e.Message.Contains("the exchange has it and it is adopted"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task WhenTheReadBackFailsTooTheRowStaysNewNothingIsRetriedAndTheReconcilerAdoptsItNextRound()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-unknown@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture, new() { ["Execution:ProtectiveOrderAttempts"] = "2" });
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        account.AfterPlaceConditional = o => o.Type == OrderType.StopMarket ? Timeout() : null;
        account.OnQuery = _ => NetworkDown();
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);

        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);

        // One request, no second id, no flatten: the row waits for reconciliation.
        Assert.Single(account.PlacedConditional, c => c.Type == OrderType.StopMarket);
        var orders = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        var stop = Assert.Single(orders, o => o.Purpose == OrderPurpose.StopLoss);
        Assert.Equal((OrderStatus.New, (long?)null), (stop.Status, stop.ExchangeAlgoId));
        Assert.DoesNotContain(orders, o => o.Purpose == OrderPurpose.Close);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read back either"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("nothing is retried or flattened"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Null((await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!.ClosedAt);

        account.AfterPlaceConditional = null;
        account.OnQuery = null;
        account.SetPosition("BTCUSDT", 0.1m, 100m);
        time.Advance(TimeSpan.FromMinutes(1));
        await provider.GetRequiredService<ExchangeReconciler>().RoundAsync(Ct);

        var adopted = (await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id)).Single(o => o.Id == stop.Id);
        Assert.Equal(OrderStatus.New, adopted.Status);
        Assert.NotNull(adopted.ExchangeAlgoId);
        Assert.Single(account.PlacedConditional, c => c.Type == OrderType.StopMarket);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ALostStopTheExchangeNeverReceivedIsRecordedAsCancelledAfterTheGraceAndRetriedUnderAFreshId()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-never-received@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var lost = 0;
        // The request never reached the exchange: the book forgets the order it just stored and the caller sees a timeout.
        account.AfterPlaceConditional = o =>
        {
            if (o.Type != OrderType.StopMarket || Interlocked.Increment(ref lost) != 1)
                return null;
            account.Conditional.RemoveAll(c => c.AlgoId == o.AlgoId);
            return Timeout();
        };
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        using var ticking = new CancellationTokenSource();
        var clock = Task.Run(async () =>
        {
            while (!ticking.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
                time.Advance(TimeSpan.FromSeconds(1));
            }
        }, CancellationToken.None);

        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        await ticking.CancelAsync();
        await clock;

        // Asked twice, the second time after the receive window; then the row is cancelled and the retry takes the next sequence.
        var stops = account.PlacedConditional.Where(c => c.Type == OrderType.StopMarket).ToList();
        Assert.Equal(2, stops.Count);
        Assert.NotEqual(stops[0].ClientAlgoId, stops[1].ClientAlgoId);
        var rows = (await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id)).Where(o => o.Purpose == OrderPurpose.StopLoss).OrderBy(o => o.CreatedAt).ToList();
        Assert.Equal([OrderStatus.Canceled, OrderStatus.New], rows.Select(o => o.Status));
        Assert.Null(rows[0].ExchangeAlgoId);
        Assert.NotNull(rows[1].ExchangeAlgoId);
        Assert.Single(account.OpenConditional("BTCUSDT"), o => o.Type == OrderType.StopMarket);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("the request may still be in flight, so it is asked again"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("the exchange does not know it; the row is recorded as cancelled"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task MovingATargetOntoASiblingsReplacesOnlyThatUnitOnBothMarkets()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-sibling-target@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var spot = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var futures = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures, "ETHUSDT");
        var spotBook = ExecutionLiveSeed.Account(exchange, spot);
        var futuresBook = ExecutionLiveSeed.Account(exchange, futures);
        // Two equal units (0.05 at 110 and 0.05 at 130, the live venue splitting by the agent's partial fraction); the council moves the first target onto the second's and the second further out.
        await ExecutionLiveSeed.SetParametersAsync(_fixture, spot, ExecutionLiveSeed.Parameters(partialFraction: 0.5m));
        await ExecutionLiveSeed.SetParametersAsync(_fixture, futures, ExecutionLiveSeed.Parameters(partialFraction: 0.5m));
        await venue.PlaceEntryAsync(spot, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, spot, ExecutionLiveSeed.LastOpen)).Id), Ct);
        await venue.PlaceEntryAsync(futures, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, futures, ExecutionLiveSeed.LastOpen)).Id), Ct);
        Assert.Equal([0.05m, 0.05m], spotBook.PlacedOcos.Select(o => o.Quantity));
        var spotTrade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, spot.Id))!;
        var futuresTrade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, futures.Id))!;

        await venue.ReplaceTargetsAsync(spot, spotTrade, [130m, 140m], null, Ct);
        await venue.ReplaceTargetsAsync(futures, futuresTrade, [130m, 140m], null, Ct);

        // Spot: the OCO at 110 went and one at 140 came; the OCO at 130 was left alone, so the whole position kept a stop throughout.
        var (_, spotOpen) = await ExecutionLiveSeed.OpenAsync(_fixture, spot.Id);
        Assert.Equal([130m, 140m], spotOpen.Where(o => o.Purpose == OrderPurpose.TakeProfit).Select(o => o.Price!.Value).Order());
        Assert.Equal(0.1m, spotOpen.Where(o => o.Purpose == OrderPurpose.StopLoss).Sum(o => o.Quantity));
        Assert.Equal(3, spotBook.PlacedOcos.Count);
        Assert.Equal((0.05m, 140m, 95m), (spotBook.PlacedOcos[2].Quantity, spotBook.PlacedOcos[2].TakeProfitPrice, spotBook.PlacedOcos[2].StopPrice));
        Assert.Single(spotBook.CancelledOcos);
        Assert.Equal(2, spotBook.Open("BTCUSDT").Count(o => o.Type == OrderType.StopLossLimit));
        // Futures: one new take-profit at 140, the row at 110 cancelled, the one at 130 left alone, the stop untouched.
        var (_, futuresOpen) = await ExecutionLiveSeed.OpenAsync(_fixture, futures.Id);
        Assert.Equal([130m, 140m], futuresOpen.Where(o => o.Purpose == OrderPurpose.TakeProfit).Select(o => o.StopPrice!.Value).Order());
        Assert.Equal(4, futuresBook.PlacedConditional.Count);
        Assert.Single(futuresBook.CancelledConditional);
        Assert.Equal(3, futuresBook.OpenConditional("ETHUSDT").Count);
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ADuplicateClientIdRejectionWithBinancesCodeIsAdopted()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-duplicate@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        // The exchange already holds the order under its deterministic id (a row lost with the database, say); the fake answers -2010 "Duplicate order sent.".
        var previous = account.Place(new OrderRequest("BTCUSDT", OrderSide.Buy, OrderType.Market, ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, decision.Id, ClientOrderIds.Entry, 0), 0.1m));
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);

        var entry = await venue.PlaceEntryAsync(agent, Market(decision.Id), Ct);

        Assert.Equal(2, account.PlacedOrders.Count);
        Assert.Equal(previous.OrderId, entry.ExchangeOrderId);
        Assert.Equal(OrderStatus.Filled, entry.Status);
        Assert.NotNull(await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AProtectionRetryPlacesOnlyWhatIsMissing()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-missing@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var refused = 0;
        account.OnPlaceConditional = r => r.Type == OrderType.TakeProfitMarket && r.TriggerPrice == 130m && Interlocked.Increment(ref refused) == 1
            ? new ExchangeRequestException("Binance USDⓈ-M place algo order failed: Order would immediately trigger. (code -2021)", ExchangeErrorKind.InvalidStop, -2021)
            : null;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        using var ticking = new CancellationTokenSource();
        var clock = Task.Run(async () =>
        {
            while (!ticking.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
                time.Advance(TimeSpan.FromSeconds(1));
            }
        }, CancellationToken.None);

        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        await ticking.CancelAsync();
        await clock;

        // The second attempt placed the one target that was refused, under a fresh id; the stop and the first target were not sent again.
        Assert.Single(account.PlacedConditional, c => c.Type == OrderType.StopMarket);
        Assert.Single(account.PlacedConditional, c => c.Type == OrderType.TakeProfitMarket && c.TriggerPrice == 110m);
        var retried = account.PlacedConditional.Where(c => c.Type == OrderType.TakeProfitMarket && c.TriggerPrice == 130m).ToList();
        Assert.Equal(2, retried.Count);
        Assert.NotEqual(retried[0].ClientAlgoId, retried[1].ClientAlgoId);
        var orders = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.Equal(OrderStatus.New, Assert.Single(orders, o => o.Purpose == OrderPurpose.StopLoss).Status);
        Assert.Equal([OrderStatus.New, OrderStatus.New, OrderStatus.Rejected], orders.Where(o => o.Purpose == OrderPurpose.TakeProfit).Select(o => o.Status).Order());
        Assert.DoesNotContain(orders, o => o.Purpose == OrderPurpose.Close);
        Assert.Contains(logs.Entries, e => e.Message.Contains("only the missing ones are placed"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task FlatteningWithAPendingCloseDoesNotPlaceASecondCloseAndFuturesKeepsTheStopUntilTheCloseIsPlaced()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-flatten@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        account.FillMarketOrders = false;
        var close = await venue.PlaceCloseAsync(agent, trade, null, Ct);
        Assert.Equal(OrderStatus.New, close.Status);

        var liveAccount = await provider.GetRequiredService<LiveExchange>().ForAsync(agent, null, Ct);
        var again = await provider.GetRequiredService<ProtectionPlacer>().FlattenAsync(liveAccount, trade, Ct);

        Assert.Equal(close.Id, again!.Id);
        Assert.Single(account.PlacedOrders, o => o.Type == OrderType.Market && o.Side == OrderSide.Sell);
        // NFR-REL-03: the reduce-only close sits beside the stop; nothing protective was cancelled to make room for it.
        Assert.Empty(account.CancelledConditional);
        Assert.Single(account.OpenConditional("BTCUSDT"), o => o.Type == OrderType.StopMarket);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task TheKillSwitchCancelsEntriesFirstPlacesTheCloseBesideTheStopAndCancelsWhatIsLeft()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-kill@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        var pending = await venue.PlaceEntryAsync(agent, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(1))).Id, PositionSide.Long, OrderType.Limit, 98m, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
        var entryCancelledFirst = false;
        var stopsStandingAtClose = -1;
        account.OnPlaceOrder = r =>
        {
            if (r.Type == OrderType.Market && r.ReduceOnly)
            {
                entryCancelledFirst = account.CancelledOrders.Any(c => c.Order.ClientOrderId == pending.ClientOrderId);
                stopsStandingAtClose = account.OpenConditional("BTCUSDT").Count(o => o.Type == OrderType.StopMarket);
            }

            return null;
        };

        var result = await provider.GetRequiredService<IAgentService>().StopAsync(user.Id, agent.Id, closePositions: true, Ct);

        Assert.Equal(AgentActionStatus.Succeeded, result.Status);
        Assert.True(entryCancelledFirst);
        Assert.Equal(1, stopsStandingAtClose);
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((0m, TradeExitReason.KillSwitch), (trade.RemainingSize, trade.ExitReason));
        Assert.NotNull(trade.ClosedAt);
        Assert.Empty((await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id)).Orders);
        Assert.Empty(account.OpenConditional("BTCUSDT"));
        Assert.Empty(account.Open("BTCUSDT"));
        Assert.Equal(AgentState.Stopped, (await AgentsLiveSeed.AgentAsync(_fixture, agent.Id)).State);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task TheReconcilerProtectsOnlyTheUncoveredUnitOfAnUnderProtectedSpotTrade()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-underprotected@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        var first = open.Single(o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 110m);
        var second = open.Single(o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 130m);
        // The first unit's OCO vanished on the exchange; the second one is whole.
        account.CancelOco("BTCUSDT", first.ListClientOrderId!);
        account.Balances.Add(new AssetBalance("BTC", 0.1m, 0m, 0.1m, null));
        time.Advance(TimeSpan.FromMinutes(1));

        await provider.GetRequiredService<ExchangeReconciler>().RoundAsync(Ct);

        Assert.Equal(3, account.PlacedOcos.Count);
        Assert.Equal((0.033m, 130m, 95m), (account.PlacedOcos[2].Quantity, account.PlacedOcos[2].TakeProfitPrice, account.PlacedOcos[2].StopPrice));
        Assert.Single(account.CancelledOcos);
        var rows = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.All(rows.Where(o => o.ListClientOrderId == first.ListClientOrderId), o => Assert.Equal(OrderStatus.Canceled, o.Status));
        Assert.All(rows.Where(o => o.ListClientOrderId == second.ListClientOrderId), o => Assert.Equal(OrderStatus.New, o.Status));
        Assert.Equal(0.1m, rows.Where(o => o.Purpose == OrderPurpose.StopLoss && o.Status == OrderStatus.New).Sum(o => o.Quantity));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("is protected for 0.067 of 0.1"));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ASpotCancelFailureMidwayKeepsTheOldStopOnThatUnitAndProtectsTheFreedOne()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-cancel-midway@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        var first = open.Single(o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 110m).ListClientOrderId!;
        var second = open.Single(o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 130m).ListClientOrderId!;
        account.OnCancelOco = id => id == second ? Timeout() : null;

        var ex = await Assert.ThrowsAsync<ExecutionUnavailableException>(() => venue.ReplaceStopAsync(agent, trade, 97m, "trail", null, Ct));

        Assert.Contains("could not be cancelled and keep the previous stop", ex.Message);
        Assert.Equal(3, account.PlacedOcos.Count);
        Assert.Equal((0.033m, 110m, 97m), (account.PlacedOcos[2].Quantity, account.PlacedOcos[2].TakeProfitPrice, account.PlacedOcos[2].StopPrice));
        var rows = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.All(rows.Where(o => o.ListClientOrderId == first), o => Assert.Equal(OrderStatus.Canceled, o.Status));
        Assert.All(rows.Where(o => o.ListClientOrderId == second), o => Assert.Equal(OrderStatus.New, o.Status));
        Assert.Equal([95m, 97m], rows.Where(o => o.Purpose == OrderPurpose.StopLoss && o.Status == OrderStatus.New).Select(o => o.StopPrice!.Value).Order());
        Assert.DoesNotContain(rows, o => o.Purpose == OrderPurpose.Close);
        Assert.Equal(95m, (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!.CurrentStop);
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ASpotExitWhoseOutcomeIsUnknownIsNotReprotectedAndTheReconcilerSettlesIt()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "rel-exit-lost@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        account.AfterPlaceOrder = o => o.Side == OrderSide.Sell && o.Type == OrderType.Market ? Timeout() : null;
        account.OnQuery = _ => NetworkDown();

        await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => venue.PlaceCloseAsync(agent, trade, null, Ct));

        // The sell may have executed, so nothing is protected again; the close row waits for reconciliation.
        Assert.Equal(2, account.PlacedOcos.Count);
        Assert.Equal(2, account.CancelledOcos.Count);
        var rows = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.Equal(OrderStatus.New, Assert.Single(rows, o => o.Purpose == OrderPurpose.Close).Status);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("may have executed; it is not protected again"));

        account.AfterPlaceOrder = null;
        account.OnQuery = null;
        time.Advance(TimeSpan.FromMinutes(1));
        await provider.GetRequiredService<ExchangeReconciler>().RoundAsync(Ct);

        var closed = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal((0m, TradeExitReason.KillSwitch), (closed.RemainingSize, closed.ExitReason));
        Assert.Equal(OrderStatus.Filled, (await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id)).Single(o => o.Purpose == OrderPurpose.Close).Status);
        Assert.Single(account.PlacedOrders, o => o.Side == OrderSide.Sell);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
