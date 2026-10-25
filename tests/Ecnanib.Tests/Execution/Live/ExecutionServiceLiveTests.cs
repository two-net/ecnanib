using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>FR-EXE-03, FR-EXE-04, FR-EXE-06 and FR-EXE-09 through the execution service over the paper venue against PostgreSQL.</summary>
[Trait("Category", "Live")]
public class ExecutionServiceLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public ExecutionServiceLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    [Fact]
    public async Task AnEntryIsSizedFromEquityRiskAndStopDistanceAndPlacedWithItsTimeout()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-open@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var execution = provider.GetRequiredService<IExecutionService>();
        var request = await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, decision.Evaluation);

        var result = await execution.ExecuteAsync(request, decision, Ct, Ct);

        var order = Assert.Single(result.Orders);
        Assert.Equal((OrderPurpose.Entry, OrderType.Limit, OrderSide.Buy, 100m, 95m, 20m, OrderStatus.New, true), (order.Purpose, order.Type, order.Side, order.Price, order.StopPrice, order.Quantity, order.Status, order.IsPaper));
        Assert.Equal(ExecutionLiveSeed.LastClose.AddHours(1), order.ExpiresAt);
        Assert.Equal("Limit long 20 BTCUSDT at 100 (stop 95), expires 01:00 UTC.", result.Note);
        Assert.Null(result.Fault);
        Assert.Equal(20m, decision.Size);
        var stored = await ExecutionLiveSeed.DecisionAsync(_fixture, decision.Id);
        Assert.Equal(20m, stored.Size);
        Assert.Equal(DecisionAction.OpenLong, stored.FinalAction);
        var checks = ExecutionLiveSeed.Checks(stored);
        Assert.Equal(["dailyLoss", "size", "notional", "minQuantity", "minNotional", "balance"], checks.Keys);
        Assert.All(checks.Values, status => Assert.Equal("pass", status));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AnEntryUnderTheMinimumNotionalIsRejectedAndTheDecisionBecomesNoTrade()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-reject@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen, risk: 0.0001m, rationale: "Tiny.");
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var execution = provider.GetRequiredService<IExecutionService>();

        var result = await execution.ExecuteAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, decision.Evaluation), decision, Ct, Ct);

        Assert.Empty(result.Orders);
        Assert.StartsWith("Rejected by execution limits: minNotional", result.Note);
        Assert.Equal(DecisionAction.NoTrade, decision.FinalAction);
        Assert.StartsWith("Rejected by execution limits (minNotional): Tiny.", decision.Rationale);
        var stored = await ExecutionLiveSeed.DecisionAsync(_fixture, decision.Id);
        Assert.Equal(DecisionAction.NoTrade, stored.FinalAction);
        Assert.Null(stored.Size);
        Assert.Equal("fail", ExecutionLiveSeed.Checks(stored)["minNotional"]);
        await using (var db = _fixture.CreateContext())
            Assert.Equal(0, await db.Orders.CountAsync(o => o.AgentId == agent.Id, Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task TheDailyLossLimitRefusesAnEntryAndPausesTheAgentUntilTheNextUtcDay()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-daily@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        await using (var db = _fixture.CreateContext())
        {
            // A stop filled today for −400 (net of its own fee) and an entry fee of 20 paid today on a 10,000 account: a loss of 420, over the
            // 3% limit measured on the equity before the loss (10,420 × 3% = 312.6).
            db.Add(new Order
            {
                AgentId = agent.Id, ClientOrderId = "seed-loss-" + Guid.NewGuid().ToString("N")[..20], Purpose = OrderPurpose.StopLoss, Type = OrderType.StopMarket, Side = OrderSide.Sell,
                Quantity = 1m, FilledQuantity = 1m, Status = OrderStatus.Filled, IsPaper = true, RealizedPnl = -400m, Fee = 0.1m, FilledAt = AgentsLiveSeed.Now, CreatedAt = AgentsLiveSeed.Now, UpdatedAt = AgentsLiveSeed.Now,
            });
            db.Add(new Order
            {
                AgentId = agent.Id, ClientOrderId = "seed-entry-" + Guid.NewGuid().ToString("N")[..20], Purpose = OrderPurpose.Entry, Type = OrderType.Market, Side = OrderSide.Buy,
                Quantity = 1m, FilledQuantity = 1m, Status = OrderStatus.Filled, IsPaper = true, Fee = 20m, FilledAt = AgentsLiveSeed.Now, CreatedAt = AgentsLiveSeed.Now, UpdatedAt = AgentsLiveSeed.Now,
            });
            await db.SaveChangesAsync(Ct);
        }

        var (provider, logs, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e => { lock (events) events.Add(e); return ValueTask.CompletedTask; });

        var result = await provider.GetRequiredService<IExecutionService>().ExecuteAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, decision.Evaluation), decision, Ct, Ct);

        Assert.Empty(result.Orders);
        Assert.StartsWith("Rejected by execution limits: dailyLoss", result.Note);
        var paused = await ExecutionLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal((AgentState.Paused, PauseCause.DailyLoss), (paused.State, paused.PauseCause));
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero), paused.AutoResumeAt);
        Assert.StartsWith("Daily loss limit of 3% reached (−420); paused until 2026-01-06 00:00 UTC.", paused.StateReason);
        await Wait.UntilAsync(() => events.OfType<AgentStateChanged>().Any(e => e.State == AgentState.Paused), "paused event");
        Assert.Contains(logs.Entries, e => e.Message.Contains("paused on its daily loss limit"));
        var status = await provider.GetRequiredService<IDailyLossLimit>().CheckAsync(agent.Id, Ct);
        Assert.Equal((true, 420m, 312.6m, false), (status.Reached, status.LossToday, status.LimitAmount, status.Paused));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ModifyTightensTheStopReplacesTheTargetAndTakesAPartial()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-modify@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var opening = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-2));
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, opening);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen, DecisionAction.Modify, null, null, 97m, [125m], partial: 0.5m, tradeId: trade.Id);
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;

        var result = await provider.GetRequiredService<IExecutionService>().ExecuteAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, decision.Evaluation, close: 110m), decision, Ct, Ct);

        Assert.Equal("Stop moved to 97. Targets set to 125. Partial of 0.05 taken at market.", result.Note);
        var orders = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        var stop = Assert.Single(orders, o => o.Purpose == OrderPurpose.StopLoss && o.Status == OrderStatus.New);
        Assert.Equal((97m, 0.1m), (stop.StopPrice, stop.Quantity));
        Assert.Single(orders, o => o.Purpose == OrderPurpose.StopLoss && o.Status == OrderStatus.Canceled);
        Assert.Equal(125m, Assert.Single(orders, o => o.Purpose == OrderPurpose.TakeProfit && o.Status == OrderStatus.New).Price);
        Assert.Equal(0.05m, Assert.Single(orders, o => o.Purpose == OrderPurpose.PartialClose).Quantity);
        var updated = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal(97m, updated.CurrentStop);
        Assert.Contains("\"council\"", updated.StopHistory.GetRawText());
        Assert.Equal("[125]", updated.Targets.GetRawText());
        var checks = ExecutionLiveSeed.Checks(await ExecutionLiveSeed.DecisionAsync(_fixture, decision.Id));
        Assert.Equal(("pass", "pass"), (checks["stopTightenOnly"], checks["partialFraction"]));

        // A stop that would not tighten, or would trigger against the last close, stays.
        var loosening = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(1), DecisionAction.Modify, null, null, 96m, [], tradeId: trade.Id);
        var kept = await provider.GetRequiredService<IExecutionService>().ExecuteAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, loosening.Evaluation, close: 110m), loosening, Ct, Ct);
        Assert.Equal("The stop stays.", kept.Note);
        Assert.Equal(97m, (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!.CurrentStop);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task CloseClosesAtMarketOnceHoweverOftenItIsDecided()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-close@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var opening = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-2));
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, opening);
        var first = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen, DecisionAction.Close, null, null, null, [], tradeId: trade.Id);
        var second = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(1), DecisionAction.Close, null, null, null, [], tradeId: trade.Id);
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var execution = provider.GetRequiredService<IExecutionService>();

        var one = await execution.ExecuteAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, first.Evaluation), first, Ct, Ct);
        var two = await execution.ExecuteAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, second.Evaluation), second, Ct, Ct);

        Assert.Equal("Closing the position at market.", one.Note);
        Assert.Equal(Assert.Single(one.Orders).Id, Assert.Single(two.Orders).Id);
        Assert.Equal((OrderPurpose.Close, 0.1m, true), (one.Orders[0].Purpose, one.Orders[0].Quantity, one.Orders[0].ReduceOnly));
        Assert.Single(await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id), o => o.Purpose == OrderPurpose.Close);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ManagementExpiresAnEntryAtItsTimeoutAndFallsBackToMarketWhenTheStrategyAsks()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-expire@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        await ExecutionLiveSeed.SetParametersAsync(_fixture, agent, ExecutionLiveSeed.Parameters(marketFallback: true));
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-1));
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);
        var entry = await venue.PlaceEntryAsync(agent, new EntryPlacement(decision.Id, PositionSide.Long, OrderType.Limit, 98m, 20m, 95m, [110m, 130m], 0.33m, ExecutionLiveSeed.LastClose), Ct);
        var evaluation = await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        var execution = provider.GetRequiredService<IExecutionService>();

        var managed = await execution.ManageAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, evaluation), Ct, Ct);

        Assert.True(managed.EntryPlaced);
        Assert.Equal("The limit entry expired unfilled. Market fallback placed for 20 BTCUSDT.", managed.Note);
        var orders = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.Equal(OrderStatus.Expired, orders.Single(o => o.Id == entry.Id).Status);
        var fallback = Assert.Single(managed.Orders);
        Assert.Equal((OrderPurpose.Entry, OrderType.Market, 20m, 95m, OrderStatus.New), (fallback.Purpose, fallback.Type, fallback.Quantity, fallback.StopPrice, fallback.Status));
        Assert.NotEqual(entry.ClientOrderId, fallback.ClientOrderId);
        Assert.Null(fallback.ExpiresAt);
        var checks = ExecutionLiveSeed.Checks(await ExecutionLiveSeed.DecisionAsync(_fixture, decision.Id));
        Assert.Equal("pass", checks["marketFallback"]);

        // Without the parameter the entry only expires.
        var plain = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "ETH", AgentState.Running, symbol: "ETHUSDT");
        var other = await ExecutionLiveSeed.NewDecisionAsync(_fixture, plain, ExecutionLiveSeed.LastOpen.AddHours(-1));
        await venue.PlaceEntryAsync(plain, new EntryPlacement(other.Id, PositionSide.Long, OrderType.Limit, 98m, 20m, 95m, [110m], 0.5m, ExecutionLiveSeed.LastClose), Ct);
        var untouched = await execution.ManageAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, plain, await ExecutionLiveSeed.NewEvaluationAsync(_fixture, plain, ExecutionLiveSeed.LastOpen)), Ct, Ct);
        Assert.False(untouched.EntryPlaced);
        Assert.Equal("The limit entry expired unfilled.", untouched.Note);
        Assert.Empty(untouched.Orders);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ManagementMovesTheStopToBreakevenAndTrailsOnceThePartialFilledAndLeavesUnmanagedTradesAlone()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-trail@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var opening = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-2));
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, opening);
        await using (var db = _fixture.CreateContext())
            await db.Trades.Where(t => t.Id == trade.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RemainingSize, 0.05m), Ct);
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var execution = provider.GetRequiredService<IExecutionService>();
        var overrides = ExecutionLiveSeed.Parameters(stopBuffer: 1m);

        // Breakeven first: the trail (101 − 2) is below the entry.
        var breakeven = await execution.ManageAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, ExecutionLiveSeed.LastOpen),
            close: 104m, levels: [new PriceLevel("trailLong", 101m)], overrides: overrides), Ct, Ct);
        Assert.Equal("Stop breakeven: 100.", breakeven.Note);
        Assert.Equal(100m, (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!.CurrentStop);

        // Then the trail: 108 − 2 = 106, under the close of 110.
        var trail = await execution.ManageAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(1)),
            close: 110m, levels: [new PriceLevel("trailLong", 108m)], overrides: overrides), Ct, Ct);
        Assert.Equal("Stop trail: 106.", trail.Note);
        var updated = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal(106m, updated.CurrentStop);
        Assert.Contains("\"breakeven\"", updated.StopHistory.GetRawText());
        Assert.Contains("\"trail\"", updated.StopHistory.GetRawText());
        var stops = (await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id)).Where(o => o.Purpose == OrderPurpose.StopLoss).ToList();
        Assert.Equal(106m, Assert.Single(stops, s => s.Status == OrderStatus.New).StopPrice);
        Assert.Equal(2, stops.Count(s => s.Status == OrderStatus.Canceled));

        // A lower close leaves it; an unmanaged trade is never touched.
        var same = await execution.ManageAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(2)),
            close: 105m, levels: [new PriceLevel("trailLong", 108m)], overrides: overrides), Ct, Ct);
        Assert.Null(same.Note);
        await using (var db = _fixture.CreateContext())
            await db.Trades.Where(t => t.Id == trade.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsUnmanaged, true), Ct);
        var unmanaged = await execution.ManageAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(3)),
            close: 120m, levels: [new PriceLevel("trailLong", 118m)], overrides: overrides), Ct, Ct);
        Assert.Null(unmanaged.Note);
        Assert.Equal(106m, (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!.CurrentStop);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task APartialThatWouldLeaveDustBehindIsSkipped()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-dust@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var opening = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-2));
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, opening);
        // 0.06 of 0.1 at 110 clears the minimums on its own, but the 0.04 left (4.40) would be under the 5.00 minimum notional nothing could ever close.
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen, DecisionAction.Modify, null, null, null, [], partial: 0.6m, tradeId: trade.Id);
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;

        var result = await provider.GetRequiredService<IExecutionService>().ExecuteAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, decision.Evaluation, close: 110m), decision, Ct, Ct);

        Assert.Equal("No partial: the remainder would be below the exchange minimum.", result.Note);
        Assert.Empty(result.Orders);
        Assert.DoesNotContain(await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id), o => o.Purpose == OrderPurpose.PartialClose);
        Assert.Equal(0.1m, (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!.RemainingSize);
        var check = Assert.Single(ExecutionLiveSeed.Checks(await ExecutionLiveSeed.DecisionAsync(_fixture, decision.Id)));
        Assert.Equal(("partialFraction", "clamped"), (check.Key, check.Value));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ManagementKeepsAPlacedFallbackInItsResultWhenALaterStepFails()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-resilient@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        await ExecutionLiveSeed.SetParametersAsync(_fixture, agent, ExecutionLiveSeed.Parameters(marketFallback: true));
        await ExecutionLiveSeed.SetMaxOpenPositionsAsync(_fixture, agent, 3);
        var opening = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-3));
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, opening);
        await using (var db = _fixture.CreateContext())
            await db.Trades.Where(t => t.Id == trade.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RemainingSize, 0.05m), Ct); // the partial filled, so breakeven is due
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-1));
        var (provider, logs, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);
        await venue.PlaceEntryAsync(agent, new EntryPlacement(decision.Id, PositionSide.Long, OrderType.Limit, 98m, 1m, 95m, [110m, 130m], 0.33m, ExecutionLiveSeed.LastClose), Ct);
        var request = await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, ExecutionLiveSeed.LastOpen), close: 110m);

        // The expired entry falls back to market first; then the breakeven move blows up in the venue.
        var managed = await provider.GetRequiredService<IExecutionService>().ManageAsync(request with { Venue = new FailingStopVenue(venue) }, Ct, Ct);

        Assert.True(managed.EntryPlaced);
        Assert.Single(managed.Orders, o => o.Purpose == OrderPurpose.Entry && o.Type == OrderType.Market);
        Assert.Contains("Market fallback placed for", managed.Note);
        Assert.Contains("Moving the stop failed; see the log.", managed.Note);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("moving the stop failed"));
        Assert.Equal(90m, (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!.CurrentStop);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AnEntryThatExpiredThisCandleNoLongerCountsAgainstTheNextFallbacksPositionCap()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "exec-expired-count@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        await ExecutionLiveSeed.SetParametersAsync(_fixture, agent, ExecutionLiveSeed.Parameters(marketFallback: true));
        await ExecutionLiveSeed.SetMaxOpenPositionsAsync(_fixture, agent, 2);
        await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-3)));
        var first = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-2));
        var second = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen.AddHours(-1));
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Paper);
        await venue.PlaceEntryAsync(agent, new EntryPlacement(first.Id, PositionSide.Long, OrderType.Limit, 98m, 1m, 95m, [110m, 130m], 0.33m, ExecutionLiveSeed.LastClose), Ct);
        await venue.PlaceEntryAsync(agent, new EntryPlacement(second.Id, PositionSide.Long, OrderType.Limit, 97m, 1m, 95m, [110m, 130m], 0.33m, ExecutionLiveSeed.LastClose), Ct);

        var managed = await provider.GetRequiredService<IExecutionService>().ManageAsync(await ExecutionLiveSeed.RequestAsync(_fixture, provider, agent, await ExecutionLiveSeed.NewEvaluationAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)), Ct, Ct);

        // With the trade and the other entry still pending, the first expiry cannot fall back (2 of 2 open); once both expired, the second can (1 of 2).
        Assert.True(managed.EntryPlaced);
        Assert.Single(managed.Orders);
        Assert.True(managed.Note!.Split("No market fallback (marketFallback, openPositions).").Length == 2, managed.Note);
        Assert.True(managed.Note.Split("Market fallback placed for 20 BTCUSDT.").Length == 2, managed.Note);
        var outcomes = new[] { first.Id, second.Id }.Select(async id => ExecutionLiveSeed.Checks(await ExecutionLiveSeed.DecisionAsync(_fixture, id))["marketFallback"]).Select(t => t.Result).Order().ToList();
        Assert.Equal(["fail", "pass"], outcomes);

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    /// <summary>The paper venue with a stop replacement that blows up the way an exchange error would.</summary>
    private sealed class FailingStopVenue(IOrderVenue inner) : IOrderVenue
    {
        public AgentMode Mode => inner.Mode;

        public Task<Order> PlaceEntryAsync(Agent agent, EntryPlacement placement, CancellationToken cancellationToken) => inner.PlaceEntryAsync(agent, placement, cancellationToken);

        public Task<Order> PlaceCloseAsync(Agent agent, Trade trade, Guid? decisionId, CancellationToken cancellationToken) => inner.PlaceCloseAsync(agent, trade, decisionId, cancellationToken);

        public Task<Order> PlacePartialCloseAsync(Agent agent, Trade trade, decimal quantity, Guid decisionId, CancellationToken cancellationToken) => inner.PlacePartialCloseAsync(agent, trade, quantity, decisionId, cancellationToken);

        public Task<IReadOnlyList<Order>> ReplaceStopAsync(Agent agent, Trade trade, decimal stop, string reason, Guid? decisionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The venue is down.");

        public Task<IReadOnlyList<Order>> ReplaceTargetsAsync(Agent agent, Trade trade, IReadOnlyList<decimal> targets, Guid? decisionId, CancellationToken cancellationToken) => inner.ReplaceTargetsAsync(agent, trade, targets, decisionId, cancellationToken);

        public Task<Order> ExpireEntryAsync(Agent agent, Order entry, CancellationToken cancellationToken) => inner.ExpireEntryAsync(agent, entry, cancellationToken);

        public Task<int> CancelOpenOrdersAsync(Agent agent, CancellationToken cancellationToken) => inner.CancelOpenOrdersAsync(agent, cancellationToken);

        public Task<int> ClosePositionsAtMarketAsync(Agent agent, CancellationToken cancellationToken) => inner.ClosePositionsAtMarketAsync(agent, cancellationToken);

        public Task<decimal> GetEquityAsync(Agent agent, CancellationToken cancellationToken) => inner.GetEquityAsync(agent, cancellationToken);

        public Task<decimal> GetAvailableAsync(Agent agent, CancellationToken cancellationToken) => inner.GetAvailableAsync(agent, cancellationToken);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
