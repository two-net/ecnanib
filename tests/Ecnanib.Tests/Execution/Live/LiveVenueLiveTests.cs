using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Services;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>FR-EXE-01, FR-EXE-02, FR-EXE-05, FR-EXE-07, FR-EXE-10 and FR-EXE-11 on the live venue, with the exchange faked by the scripted account book and the rows in PostgreSQL.</summary>
[Trait("Category", "Live")]
public class LiveVenueLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public LiveVenueLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    private static EntryPlacement Market(Guid decisionId, decimal quantity = 0.1m, decimal stop = 95m, PositionSide side = PositionSide.Long, decimal fraction = 0.33m) =>
        new(decisionId, side, OrderType.Market, null, quantity, stop, [110m, 130m], fraction, null);

    [Fact]
    public async Task ASpotMarketEntryFillsOpensTheTradeAndProtectsEachUnitWithAnOco()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-spot@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);

        var entry = await venue.PlaceEntryAsync(agent, Market(decision.Id), Ct);
        var again = await venue.PlaceEntryAsync(agent, Market(decision.Id), Ct);

        Assert.Equal(entry.Id, again.Id);
        Assert.Single(account.PlacedOrders);
        Assert.Equal((OrderStatus.Filled, 0.1m, 100m, false), (entry.Status, entry.FilledQuantity, entry.AverageFillPrice, entry.IsPaper));
        Assert.StartsWith("e", entry.ClientOrderId, StringComparison.Ordinal);
        Assert.NotNull(entry.ExchangeOrderId);
        var accepted = Assert.Single(logs.Entries, e => e.Message == $"Order {entry.ClientOrderId} of agent {agent.Id} accepted by the exchange as {entry.ExchangeOrderId} with status Filled");
        Assert.Equal(new Dictionary<string, object?> { ["AgentId"] = agent.Id, ["UserId"] = user.Id, ["DecisionId"] = decision.Id, ["OrderId"] = entry.Id }, accepted.Scope);
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((100m, 0.1m, 0.1m, 95m, 0.01m, false), (trade.EntryPrice, trade.Size, trade.RemainingSize, trade.CurrentStop, trade.Fees, trade.IsPaper));
        Assert.Equal([110m, 130m], TradeJson.Targets(trade.Targets));
        Assert.Equal(2, account.PlacedOcos.Count);
        Assert.Equal([0.033m, 0.067m], account.PlacedOcos.Select(o => o.Quantity));
        Assert.Equal([110m, 130m], account.PlacedOcos.Select(o => o.TakeProfitPrice));
        Assert.All(account.PlacedOcos, o => Assert.Equal((95m, 94.52m), (o.StopPrice, o.StopLimitPrice)));
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        Assert.Equal(2, open.Count(o => o.Purpose == OrderPurpose.TakeProfit && o.Type == OrderType.LimitMaker));
        Assert.Equal(2, open.Count(o => o.Purpose == OrderPurpose.StopLoss && o.Type == OrderType.StopLossLimit && o.StopPrice == 95m && o.Price == 94.52m));
        Assert.All(open, o => Assert.NotNull(o.ListClientOrderId));
        Assert.All(open, o => Assert.NotNull(o.OrderListId));
        Assert.Equal(10_010m, await venue.GetEquityAsync(agent, Ct));
        Assert.Equal(10_000m, await venue.GetAvailableAsync(agent, Ct));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AgentsLiveSeed.ExchangeSecret));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AFuturesEntrySetsTheAccountUpOnceAndProtectsWithAReduceOnlyStopAndATakeProfitPerUnit()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-futures@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures, "ETHUSDT");
        var account = ExecutionLiveSeed.Account(exchange, agent);
        account.FeeRate = 0.0005m;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);

        var first = await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id, side: PositionSide.Short, stop: 105m), Ct);

        Assert.Equal([("ETHUSDT", MarginType.Isolated)], account.MarginSets);
        Assert.Equal([("ETHUSDT", 3)], account.LeverageSets);
        Assert.Equal(OrderStatus.Filled, first.Status);
        Assert.Equal(3, account.PlacedConditional.Count);
        var stop = Assert.Single(account.PlacedConditional, c => c.Type == OrderType.StopMarket);
        Assert.Equal((OrderSide.Buy, 105m, 0.1m, true, false, true), (stop.Side, stop.TriggerPrice, stop.Quantity, stop.ReduceOnly, stop.ClosePosition, stop.PriceProtect));
        Assert.Equal([(110m, 0.033m), (130m, 0.067m)], account.PlacedConditional.Where(c => c.Type == OrderType.TakeProfitMarket).Select(c => (c.TriggerPrice, c.Quantity ?? 0m)));
        var (trades, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        Assert.Equal((PositionSide.Short, 0.1m, 0.005m), (trades[0].Side, trades[0].RemainingSize, trades[0].Fees));
        Assert.All(open.Where(o => o.Purpose is OrderPurpose.StopLoss or OrderPurpose.TakeProfit), o => Assert.NotNull(o.ExchangeAlgoId));
        Assert.True(open.Single(o => o.Purpose == OrderPurpose.StopLoss).ReduceOnly);
        Assert.Equal(10_000m, await venue.GetEquityAsync(agent, Ct));

        // The account setup is remembered for the process; hedge mode switched on later is a fault with guidance.
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(1))).Id, side: PositionSide.Short, stop: 105m), Ct);
        Assert.Single(account.LeverageSets);
        account.PositionMode = PositionMode.Hedge;
        var hedged = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "hedged", AgentState.Running, AgentMode.Live, MarketType.UsdFutures, "BTCUSDT", exchangeKeyId: agent.ExchangeKeyId);
        var hedgedDecision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, hedged, ExecutionLiveSeed.LastOpen);
        var fault = await Assert.ThrowsAsync<ExecutionFaultException>(() => venue.PlaceEntryAsync(hedged, Market(hedgedDecision.Id), Ct));
        Assert.Contains("Hedge mode", fault.Message);
        Assert.Contains("one-way position mode", fault.Message);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ReplacingAStopPlacesTheNewFuturesStopBeforeTheOldGoesAndReplacesTheSpotOcos()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-stop@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var futures = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var spot = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var futuresBook = ExecutionLiveSeed.Account(exchange, futures);
        var spotBook = ExecutionLiveSeed.Account(exchange, spot);
        await venue.PlaceEntryAsync(futures, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, futures, ExecutionLiveSeed.LastOpen)).Id), Ct);
        await venue.PlaceEntryAsync(spot, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, spot, ExecutionLiveSeed.LastOpen)).Id), Ct);
        var futuresTrade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, futures.Id))!;
        var spotTrade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, spot.Id))!;
        var openStopsAtCancel = -1;
        futuresBook.OnCancel = _ =>
        {
            openStopsAtCancel = futuresBook.OpenConditional("BTCUSDT").Count(o => o.Type == OrderType.StopMarket);
            return null;
        };

        var newStops = await venue.ReplaceStopAsync(futures, futuresTrade, 97m, "trail", null, Ct);
        var newSpot = await venue.ReplaceStopAsync(spot, spotTrade, 97m, "breakeven", null, Ct);

        Assert.Equal(2, openStopsAtCancel);
        Assert.Equal(97m, Assert.Single(newStops).StopPrice);
        Assert.Equal(97m, Assert.Single(futuresBook.OpenConditional("BTCUSDT"), o => o.Type == OrderType.StopMarket).TriggerPrice);
        Assert.Single(futuresBook.CancelledConditional);
        var updated = (await ExecutionLiveSeed.TradeOfAsync(_fixture, futures.Id))!;
        Assert.Equal(97m, updated.CurrentStop);
        Assert.Contains("\"trail\"", updated.StopHistory.GetRawText());

        Assert.Equal(2, spotBook.CancelledOcos.Count);
        Assert.Equal(4, spotBook.PlacedOcos.Count);
        Assert.All(spotBook.PlacedOcos.Skip(2), o => Assert.Equal(97m, o.StopPrice));
        Assert.Equal(4, newSpot.Count);
        var (_, spotOpen) = await ExecutionLiveSeed.OpenAsync(_fixture, spot.Id);
        Assert.Equal([97m, 97m], spotOpen.Where(o => o.Purpose == OrderPurpose.StopLoss).Select(o => o.StopPrice));
        Assert.Equal(97m, (await ExecutionLiveSeed.TradeOfAsync(_fixture, spot.Id))!.CurrentStop);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ASpotExitCancelsTheProtectionFirstAndAPartialProtectsWhatStays()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-exit@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var closing = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "closing", MarketType.Spot);
        var partial = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "partial", MarketType.Spot, "ETHUSDT");
        var closingBook = ExecutionLiveSeed.Account(exchange, closing);
        var partialBook = ExecutionLiveSeed.Account(exchange, partial);
        var closeDecision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, closing, ExecutionLiveSeed.LastOpen);
        var partialDecision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, partial, ExecutionLiveSeed.LastOpen);
        await venue.PlaceEntryAsync(closing, Market(closeDecision.Id), Ct);
        await venue.PlaceEntryAsync(partial, Market(partialDecision.Id), Ct);
        closingBook.MarketPrice = 120m;
        partialBook.MarketPrice = 112m;

        var close = await venue.PlaceCloseAsync(closing, (await ExecutionLiveSeed.TradeOfAsync(_fixture, closing.Id))!, closeDecision.Id, Ct);
        var taken = await venue.PlacePartialCloseAsync(partial, (await ExecutionLiveSeed.TradeOfAsync(_fixture, partial.Id))!, 0.05m, partialDecision.Id, Ct);

        Assert.Equal(2, closingBook.CancelledOcos.Count);
        Assert.Equal((OrderPurpose.Close, OrderStatus.Filled, 0.1m), (close.Purpose, close.Status, close.FilledQuantity));
        var closed = (await ExecutionLiveSeed.TradeOfAsync(_fixture, closing.Id))!;
        Assert.Equal((TradeExitReason.Council, 0m, 120m), (closed.ExitReason, closed.RemainingSize, closed.ExitPrice));
        Assert.Equal(20m * 0.1m - 0.01m - 0.012m, closed.RealizedPnl);
        Assert.Equal(4m, closed.RMultiple);
        Assert.NotNull(closed.ClosedAt);
        Assert.Empty((await ExecutionLiveSeed.OpenAsync(_fixture, closing.Id)).Orders);

        Assert.Equal((OrderPurpose.PartialClose, OrderStatus.Filled, 0.05m), (taken.Purpose, taken.Status, taken.FilledQuantity));
        Assert.Equal(2, partialBook.CancelledOcos.Count);
        Assert.Equal(4, partialBook.PlacedOcos.Count);
        Assert.Equal([0.033m, 0.017m], partialBook.PlacedOcos.Skip(2).Select(o => o.Quantity));
        var reduced = (await ExecutionLiveSeed.TradeOfAsync(_fixture, partial.Id))!;
        Assert.Equal((0.05m, 0.1m), (reduced.RemainingSize, reduced.Size));
        Assert.Null(reduced.ClosedAt);
        Assert.Equal(-0.01m + 12m * 0.05m - 112m * 0.05m * 0.001m, reduced.RealizedPnl);
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, partial.Id);
        Assert.Equal([0.033m, 0.017m], open.Where(o => o.Purpose == OrderPurpose.TakeProfit).Select(o => o.Quantity));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ProtectionThatCannotBePlacedIsRetriedThenThePositionIsClosedAtMarketAndLoggedCritical()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-flatten@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture, new() { ["Execution:ProtectiveOrderAttempts"] = "2" });
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        account.OnPlaceOco = _ => new ExchangeRequestException("Binance Spot place oco failed: insufficient balance (code -2010)", ExchangeErrorKind.RejectedOrder, -2010);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);

        // The retry delay runs on the fake clock, which a side task advances while the placement is in flight.
        using var ticking = new CancellationTokenSource();
        var clock = Task.Run(async () =>
        {
            while (!ticking.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
                time.Advance(TimeSpan.FromSeconds(1));
            }
        }, CancellationToken.None);
        await venue.PlaceEntryAsync(agent, Market(decision.Id), Ct);
        await ticking.CancelAsync();
        await clock;

        Assert.Equal(2, account.PlacedOcos.Count);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("could not be placed after 2 attempt(s); the position is closed at market"));
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((0m, TradeExitReason.KillSwitch), (trade.RemainingSize, trade.ExitReason));
        Assert.NotNull(trade.ClosedAt);
        var orders = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.Equal(OrderStatus.Filled, Assert.Single(orders, o => o.Purpose == OrderPurpose.Close).Status);
        Assert.All(orders.Where(o => o.Purpose is OrderPurpose.StopLoss or OrderPurpose.TakeProfit), o => Assert.Equal(OrderStatus.Rejected, o.Status));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ExpiringAnEntryCancelsAnUnfilledLimitAndAdoptsOneThatFilledMeanwhile()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-expire@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        var unfilled = await venue.PlaceEntryAsync(agent, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Limit, 98m, 0.1m, 95m, [110m], 0.5m, ExecutionLiveSeed.LastClose), Ct);
        var filled = await venue.PlaceEntryAsync(agent, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(1))).Id, PositionSide.Long, OrderType.Limit, 99m, 0.1m, 95m, [110m], 0.5m, ExecutionLiveSeed.LastClose), Ct);
        await account.FillAsync(filled.ClientOrderId, 99m);

        var expired = await venue.ExpireEntryAsync(agent, unfilled, Ct);
        var adopted = await venue.ExpireEntryAsync(agent, filled, Ct);

        Assert.Equal(OrderStatus.Expired, expired.Status);
        Assert.Contains(account.CancelledOrders, c => c.Order.ClientOrderId == unfilled.ClientOrderId);
        Assert.Equal((OrderStatus.Filled, 0.1m, 99m), (adopted.Status, adopted.FilledQuantity, adopted.AverageFillPrice));
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((99m, 0.1m), (trade.EntryPrice, trade.RemainingSize));
        Assert.Equal(2, account.PlacedOcos.Count);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task TheKillSwitchCancelsEveryOpenOrderAndClosesEveryPositionAtMarket()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-kill@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        await venue.PlaceEntryAsync(agent, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(1))).Id, PositionSide.Long, OrderType.Limit, 90m, 0.1m, 85m, [], 0.5m, null), Ct);

        var cancelled = await venue.CancelOpenOrdersAsync(agent, Ct);
        var closed = await venue.ClosePositionsAtMarketAsync(agent, Ct);

        Assert.Equal(4, cancelled);
        Assert.Equal(1, closed);
        Assert.Equal(3, account.CancelledConditional.Count);
        Assert.Single(account.CancelledOrders);
        Assert.Empty(account.OpenConditional(null));
        Assert.Empty(account.Open(null));
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((TradeExitReason.KillSwitch, 0m), (trade.ExitReason, trade.RemainingSize));
        Assert.Empty((await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id)).Orders);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AFuturesStopReplacementThatCannotBePlacedLeavesTheOldStopAndThePosition()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "venue-keep-stop@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture, new() { ["Execution:ProtectiveOrderAttempts"] = "2" });
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        await venue.PlaceEntryAsync(agent, Market((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id), Ct);
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        account.OnPlaceConditional = r => r.Type == OrderType.StopMarket ? new ExchangeRequestException("Binance Futures place algo order failed: timeout (code -1007)", ExchangeErrorKind.RejectedOrder, -1007) : null;
        using var ticking = new CancellationTokenSource();
        var clock = Task.Run(async () =>
        {
            while (!ticking.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
                time.Advance(TimeSpan.FromSeconds(1));
            }
        }, CancellationToken.None);

        // New before old (NFR-REL-03): the old stop is still working, so the failed replacement is reported and nothing is flattened.
        var ex = await Assert.ThrowsAsync<ExecutionUnavailableException>(() => venue.ReplaceStopAsync(agent, trade, 97m, "trail", null, Ct));
        await ticking.CancelAsync();
        await clock;

        Assert.StartsWith("The stop could not be placed:", ex.Message);
        Assert.Equal(95m, Assert.Single(account.OpenConditional("BTCUSDT"), o => o.Type == OrderType.StopMarket).TriggerPrice);
        Assert.Empty(account.CancelledConditional);
        var kept = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((95m, 0.1m), (kept.CurrentStop, kept.RemainingSize));
        Assert.Null(kept.ClosedAt);
        var orders = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.DoesNotContain(orders, o => o.Purpose == OrderPurpose.Close);
        Assert.Equal(OrderStatus.New, orders.Single(o => o.Purpose == OrderPurpose.StopLoss && o.StopPrice == 95m).Status);
        Assert.All(orders.Where(o => o.Purpose == OrderPurpose.StopLoss && o.StopPrice == 97m), o => Assert.Equal(OrderStatus.Rejected, o.Status));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
