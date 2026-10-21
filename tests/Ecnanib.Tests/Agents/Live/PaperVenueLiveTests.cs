using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Agents.Services.Paper;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents.Live;

/// <summary>FR-AGT-06, FR-AGT-11, FR-AGT-12 and FR-EXE-07 for the paper venue and the fill simulator against PostgreSQL, with prices pushed through the faked streams.</summary>
[Trait("Category", "Live")]
public class PaperVenueLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private static readonly DateTimeOffset LastOpen = new(2026, 1, 4, 23, 0, 0, TimeSpan.Zero);
    private readonly DatabaseLiveFixture _fixture;

    public PaperVenueLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMarketEntryFillsOnTheNextTickAndCreatesTheTradeWithItsProtection()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "paper-market@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen);
        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var simulator = await StartSimulatorAsync(provider, logs);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e =>
        {
            lock (events)
                events.Add(e);
            return ValueTask.CompletedTask;
        });

        var order = await venue.PlaceEntryAsync(agent, Placement(decision.Id, PositionSide.Long, OrderType.Market, null, 0.1m, 90m, 120m, null), Ct);
        var again = await venue.PlaceEntryAsync(agent, Placement(decision.Id, PositionSide.Long, OrderType.Market, null, 0.1m, 90m, 120m, null), Ct);

        Assert.Equal(order.Id, again.Id);
        Assert.Equal(PaperClientOrderId.For(agent.Id, decision.Id, PaperClientOrderId.Entry, 0), order.ClientOrderId);
        Assert.True(order.IsPaper);
        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Equal(90m, order.StopPrice);
        Assert.True(streams.HasPriceSubscription(AgentsLiveSeed.SpotBtc));
        Assert.Equal(1, simulator.BookCount);

        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 100m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => events.OfType<TradeChanged>().Any(e => e.IsOpen), "filled");

        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.AgentId == agent.Id, Ct);
            Assert.Equal(100.05m, trade.EntryPrice);
            Assert.Equal(0.1m, trade.Size);
            Assert.Equal(0.010005m, trade.Fees);
            Assert.Equal(90m, trade.CurrentStop);
            Assert.Equal("[120]", trade.Targets.GetRawText());
            Assert.Contains("\"initial\"", trade.StopHistory.GetRawText());
            Assert.True(trade.IsPaper);
            Assert.Null(trade.ClosedAt);
            Assert.Equal(decision.Id, trade.EntryDecisionId);
            var orders = await db.Orders.AsNoTracking().Where(o => o.AgentId == agent.Id).OrderBy(o => o.CreatedAt).ThenBy(o => o.Purpose).ToListAsync(Ct);
            var entry = orders.Single(o => o.Purpose == OrderPurpose.Entry);
            Assert.Equal(OrderStatus.Filled, entry.Status);
            Assert.Equal(100.05m, entry.AverageFillPrice);
            Assert.Equal(trade.Id, entry.TradeId);
            var stop = orders.Single(o => o.Purpose == OrderPurpose.StopLoss);
            Assert.Equal((OrderStatus.New, OrderType.StopMarket, OrderSide.Sell, 90m, true, trade.Id), (stop.Status, stop.Type, stop.Side, stop.StopPrice, stop.ReduceOnly, stop.TradeId));
            var target = orders.Single(o => o.Purpose == OrderPurpose.TakeProfit);
            Assert.Equal((OrderStatus.New, OrderType.Limit, 120m), (target.Status, target.Type, target.Price));
            Assert.Equal(10_000m, await venue.GetEquityAsync(agent, Ct));
        }

        await simulator.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task LimitEntriesFillAtTheirPriceWhenTradedThroughAndExpireAtTheirTimeout()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "paper-limit@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen);
        var stale = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen.AddHours(-1));
        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var simulator = await StartSimulatorAsync(provider, logs);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);

        var limit = await venue.PlaceEntryAsync(agent, Placement(decision.Id, PositionSide.Long, OrderType.Limit, 95m, 0.2m, 90m, null, null), Ct);
        var expiring = await venue.PlaceEntryAsync(agent, Placement(stale.Id, PositionSide.Long, OrderType.Limit, 80m, 0.2m, 70m, null, AgentsLiveSeed.Now.AddSeconds(-1)), Ct);
        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 96m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => Status(expiring.Id) == OrderStatus.Expired, "expired");
        Assert.Equal(OrderStatus.New, Status(limit.Id));

        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 95m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => Status(limit.Id) == OrderStatus.Filled, "filled");
        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.AgentId == agent.Id, Ct);
            Assert.Equal(95m, trade.EntryPrice);
            Assert.Equal(0.019m, trade.Fees);
            Assert.Equal("[]", trade.Targets.GetRawText());
            Assert.Null(await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.TakeProfit, Ct));
            Assert.NotNull(await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.StopLoss, Ct));
        }

        await simulator.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task StopsAndTargetsCloseTheTradeWithItsOutcomeAndCancelTheSibling()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "paper-exit@example.com");
        var stopped = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "stopped", AgentState.Running);
        var targeted = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "targeted", AgentState.Running, symbol: "ETHUSDT");
        var stoppedTrade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, stopped, await AgentsLiveSeed.NewDecisionAsync(_fixture, stopped, LastOpen));
        var targetedTrade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, targeted, await AgentsLiveSeed.NewDecisionAsync(_fixture, targeted, LastOpen));
        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var simulator = await StartSimulatorAsync(provider, logs);
        Assert.Equal(2, simulator.BookCount);
        var eth = new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT");

        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 110m, AgentsLiveSeed.Now));
        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 89m, AgentsLiveSeed.Now));
        await streams.PushPriceAsync(eth, new PriceTick("ETHUSDT", 121m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => ClosedAt(stoppedTrade.Id) is not null && ClosedAt(targetedTrade.Id) is not null, "closed");

        await using (var db = _fixture.CreateContext())
        {
            var loss = await db.Trades.AsNoTracking().SingleAsync(t => t.Id == stoppedTrade.Id, Ct);
            Assert.Equal(89.955m, loss.ExitPrice);
            Assert.Equal(TradeExitReason.Stop, loss.ExitReason);
            Assert.Equal(0.0089955m, loss.Fees);
            Assert.Equal((89.955m - 100m) * 0.1m - 0.0089955m, loss.RealizedPnl);
            Assert.Equal((89.955m - 100m) / 10m, loss.RMultiple);
            Assert.Equal(0.11m, loss.MaxAdverseExcursion);
            Assert.Equal(0.1m, loss.MaxFavorableExcursion);
            var lossOrders = await db.Orders.AsNoTracking().Where(o => o.TradeId == loss.Id).ToListAsync(Ct);
            Assert.Equal(OrderStatus.Filled, lossOrders.Single(o => o.Purpose == OrderPurpose.StopLoss).Status);
            Assert.Equal(OrderStatus.Canceled, lossOrders.Single(o => o.Purpose == OrderPurpose.TakeProfit).Status);

            var win = await db.Trades.AsNoTracking().SingleAsync(t => t.Id == targetedTrade.Id, Ct);
            Assert.Equal(120m, win.ExitPrice);
            Assert.Equal(TradeExitReason.Target, win.ExitReason);
            Assert.Equal(0.012m, win.Fees);
            Assert.Equal(20m * 0.1m - 0.012m, win.RealizedPnl);
            Assert.Equal(2m, win.RMultiple);
            var winOrders = await db.Orders.AsNoTracking().Where(o => o.TradeId == win.Id).ToListAsync(Ct);
            Assert.Equal(OrderStatus.Filled, winOrders.Single(o => o.Purpose == OrderPurpose.TakeProfit).Status);
            Assert.Equal(OrderStatus.Canceled, winOrders.Single(o => o.Purpose == OrderPurpose.StopLoss).Status);
        }

        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);
        Assert.Equal(10_000m + 20m * 0.1m - 0.012m, await venue.GetEquityAsync(targeted, Ct));
        await Wait.UntilAsync(() => simulator.BookCount == 0, "books emptied");
        Assert.Equal(0, streams.PriceSubscriptions);

        await simulator.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task SpotEntriesAreLongOnlyAndEntriesNeedAStop()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "paper-spot@example.com");
        var spot = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "spot", AgentState.Running);
        var futures = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "futures", AgentState.Running, market: MarketType.UsdFutures, symbol: "ETHUSDT");
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, futures, LastOpen);
        var (provider, logs, _, _, _) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var simulator = await StartSimulatorAsync(provider, logs);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);

        await Assert.ThrowsAsync<ArgumentException>(() => venue.PlaceEntryAsync(spot, Placement(decision.Id, PositionSide.Short, OrderType.Market, null, 0.1m, 110m, null, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => venue.PlaceEntryAsync(futures, Placement(decision.Id, PositionSide.Short, OrderType.Limit, 100m, 0.1m, 95m, null, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => venue.PlaceEntryAsync(futures, Placement(decision.Id, PositionSide.Long, OrderType.Market, null, 0.1m, 0m, null, null), Ct));
        var order = await venue.PlaceEntryAsync(futures, Placement(decision.Id, PositionSide.Short, OrderType.Limit, 100m, 0.1m, 110m, 80m, null), Ct);
        Assert.Equal(OrderSide.Sell, order.Side);
        await using (var db = _fixture.CreateContext())
            Assert.Empty(await db.Orders.Where(o => o.AgentId == spot.Id).ToListAsync(Ct));

        await simulator.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task StopEitherClosesPositionsAtMarketOrLeavesThemUnmanaged()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "paper-stop@example.com");
        var leaving = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "leaving", AgentState.Running);
        var closing = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "closing", AgentState.Paused, symbol: "ETHUSDT");
        var leftTrade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, leaving, await AgentsLiveSeed.NewDecisionAsync(_fixture, leaving, LastOpen));
        var closedTrade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, closing, await AgentsLiveSeed.NewDecisionAsync(_fixture, closing, LastOpen));
        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var simulator = await StartSimulatorAsync(provider, logs);
        var agents = provider.GetRequiredService<IAgentService>();
        var eth = new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT");

        var left = await agents.StopAsync(user.Id, leaving.Id, closePositions: false, Ct);
        Assert.Equal(AgentActionStatus.Succeeded, left.Status);
        Assert.Contains("2 order(s) cancelled; 1 position(s) left open as unmanaged", left.Detail);
        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.Id == leftTrade.Id, Ct);
            Assert.True(trade.IsUnmanaged);
            Assert.Null(trade.ClosedAt);
            Assert.All(await db.Orders.AsNoTracking().Where(o => o.TradeId == trade.Id).ToListAsync(Ct), o => Assert.Equal(OrderStatus.Canceled, o.Status));
        }

        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 50m, AgentsLiveSeed.Now));
        await Wait.SettleAsync(200);
        Assert.Null(ClosedAt(leftTrade.Id));
        var detail = await agents.GetAsync(user.Id, leaving.Id, Ct);
        Assert.True(Assert.Single(detail!.OpenPositions).IsUnmanaged);

        var close = await agents.StopAsync(user.Id, closing.Id, closePositions: true, Ct);
        Assert.Equal(AgentActionStatus.Succeeded, close.Status);
        Assert.Contains("1 position(s) closing at market", close.Detail);
        Assert.Equal(AgentState.Stopped, (await AgentsLiveSeed.AgentAsync(_fixture, closing.Id)).State);
        var closingDetail = await agents.GetAsync(user.Id, closing.Id, Ct);
        Assert.True(Assert.Single(closingDetail!.OpenPositions).IsClosing);
        Assert.Equal(OrderPurpose.Close, Assert.Single(closingDetail.OpenOrders).Purpose);

        await streams.PushPriceAsync(eth, new PriceTick("ETHUSDT", 100m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => ClosedAt(closedTrade.Id) is not null, "closed at market");
        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.Id == closedTrade.Id, Ct);
            Assert.Equal(99.95m, trade.ExitPrice);
            Assert.Equal(TradeExitReason.KillSwitch, trade.ExitReason);
            Assert.False(trade.IsUnmanaged);
            Assert.Equal(OrderStatus.Filled, (await db.Orders.AsNoTracking().SingleAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.Close, Ct)).Status);
        }

        await simulator.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task TheBooksAreRebuiltFromTheDatabase()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "paper-reload@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, LastOpen, stop: 90m, target: 130m);
        var (provider, logs, _, _, streams) = AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var simulator = await StartSimulatorAsync(provider, logs);
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);
        var order = await venue.PlaceEntryAsync(agent, Placement(decision.Id, PositionSide.Long, OrderType.Limit, 95m, 0.1m, 90m, 130m, null), Ct);

        Assert.Equal(1, await simulator.ReloadAsync(Ct));
        Assert.Equal([(MarketType.Spot, "BTCUSDT")], simulator.Symbols);
        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 94m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => Status(order.Id) == OrderStatus.Filled, "filled after reload");
        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.AgentId == agent.Id, Ct);
            Assert.Equal(0.1m, trade.RemainingSize);
            Assert.Equal(90m, (await db.Orders.AsNoTracking().SingleAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.StopLoss, Ct)).StopPrice);
            // Rebuilt from the database, the entry took the strategy's partial fraction (0.33): the partial unit at the one target, the rest trailing.
            var target = await db.Orders.AsNoTracking().SingleAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.TakeProfit, Ct);
            Assert.Equal((130m, 0.033m), (target.Price, target.Quantity));
        }

        // After a second reload the units come from the rows: the partial unit's target takes it off and the rest stays open until the stop.
        Assert.Equal(1, await simulator.ReloadAsync(Ct));
        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 131m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => RemainingSize(agent.Id) == 0.067m, "partial taken after reload");
        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.AgentId == agent.Id, Ct);
            Assert.Null(trade.ClosedAt);
            // Net of the entry fee (95 × 0.1 × 0.1%) and the exit fee.
            Assert.Equal(-0.0095m + 130m * 0.033m - 95m * 0.033m - 130m * 0.033m * 0.001m, trade.RealizedPnl);
            var target = await db.Orders.AsNoTracking().SingleAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.TakeProfit, Ct);
            Assert.Equal((OrderStatus.Filled, 0.033m), (target.Status, target.FilledQuantity));
            Assert.Equal(0.067m, (await db.Orders.AsNoTracking().SingleAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.StopLoss, Ct)).Quantity);
        }

        await streams.PushPriceAsync(AgentsLiveSeed.SpotBtc, new PriceTick("BTCUSDT", 89m, AgentsLiveSeed.Now));
        await Wait.UntilAsync(() => simulator.BookCount == 0, "remainder stopped out after reload");
        await using (var db = _fixture.CreateContext())
        {
            var trade = await db.Trades.AsNoTracking().SingleAsync(t => t.AgentId == agent.Id, Ct);
            Assert.Equal(TradeExitReason.Stop, trade.ExitReason);
            Assert.Equal(0m, trade.RemainingSize);
            Assert.Equal(2, await db.Orders.AsNoTracking().CountAsync(o => o.TradeId == trade.Id && o.Purpose != OrderPurpose.Entry && o.Status == OrderStatus.Filled, Ct));
        }

        await simulator.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    /// <summary>An entry whose whole size takes the one target (partial fraction 1), as a position too small to split is protected.</summary>
    private static EntryPlacement Placement(Guid decisionId, PositionSide side, OrderType type, decimal? price, decimal quantity, decimal stop, decimal? target, DateTimeOffset? expiresAt) =>
        new(decisionId, side, type, price, quantity, stop, target is { } t ? [t] : [], 1m, expiresAt);

    private static async Task<PaperFillSimulator> StartSimulatorAsync(ServiceProvider provider, TestLoggerProvider logs)
    {
        var simulator = provider.GetRequiredService<PaperFillSimulator>();
        await simulator.StartAsync(Ct);
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Message.StartsWith("Paper fill simulator follows", StringComparison.Ordinal) && e.Message.Contains("symbol(s)", StringComparison.Ordinal)), "loaded");
        return simulator;
    }

    private OrderStatus? Status(Guid orderId)
    {
        using var db = _fixture.CreateContext();
        return db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => (OrderStatus?)o.Status).SingleOrDefault();
    }

    private decimal? RemainingSize(Guid agentId)
    {
        using var db = _fixture.CreateContext();
        return db.Trades.AsNoTracking().Where(t => t.AgentId == agentId).Select(t => (decimal?)t.RemainingSize).SingleOrDefault();
    }

    private DateTimeOffset? ClosedAt(Guid tradeId)
    {
        using var db = _fixture.CreateContext();
        return db.Trades.AsNoTracking().Where(t => t.Id == tradeId).Select(t => t.ClosedAt).SingleOrDefault();
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
