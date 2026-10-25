using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Services.Live;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>The fill tracker on the user-data stream (EX-03, FR-EXE-01/02/12, FR-EXE-09): entries open trades, exits reduce and close them, conditional orders trigger, duplicates are no-ops.</summary>
[Trait("Category", "Live")]
public class LiveFillsLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public LiveFillsLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    [Fact]
    public async Task AStreamedEntryFillOpensTheTradeAndTargetAndStopFillsReduceThenCloseIt()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "fills-spot@example.com");
        var (provider, logs, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        account.FillMarketOrders = false;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var tracker = provider.GetRequiredService<LiveFillTracker>();
        await tracker.StartAsync(Ct);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);

        var entry = await venue.PlaceEntryAsync(agent, new EntryPlacement(decision.Id, PositionSide.Long, OrderType.Limit, 100m, 0.1m, 95m, [110m, 130m], 0.33m, ExecutionLiveSeed.LastClose.AddHours(1)), Ct);
        await Wait.UntilAsync(() => account.SubscriberCount == 1, "subscribed");
        Assert.Equal(OrderStatus.New, entry.Status);

        await account.FillAsync(entry.ClientOrderId, 100m, fee: 0.01m);
        await Wait.UntilAsync(() => account.PlacedOcos.Count == 2, "protected");
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((100m, 0.1m, 0.1m, 0.01m), (trade.EntryPrice, trade.Size, trade.RemainingSize, trade.Fees));
        var filled = Assert.Single(logs.Entries, e => e.Message.StartsWith($"Entry {entry.ClientOrderId} of agent {agent.Id} filled", StringComparison.Ordinal));
        Assert.Equal((agent.Id, user.Id, decision.Id, entry.Id), (filled.Scope["AgentId"], filled.Scope["UserId"], filled.Scope["DecisionId"], filled.Scope["OrderId"]));
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        var firstTarget = open.Single(o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 110m);
        var remainderStop = open.Single(o => o.Purpose == OrderPurpose.StopLoss && o.ListClientOrderId != firstTarget.ListClientOrderId);

        // The partial unit's target fills: the trade is reduced, its sibling stop leg expired on the exchange.
        await account.FillAsync(firstTarget.ClientOrderId, 110m, fee: 0.00363m);
        await Wait.UntilAsync(() => RemainingSize(agent.Id) == 0.067m, "reduced");
        trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Null(trade.ClosedAt);
        Assert.Equal(-0.01m + 10m * 0.033m - 0.00363m, trade.RealizedPnl);
        Assert.Equal(0.01m + 0.00363m, trade.Fees);
        var filledTarget = (await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id)).Single(o => o.Id == firstTarget.Id);
        Assert.Equal((OrderStatus.Filled, 0.033m, 0.00363m, 10m * 0.033m - 0.00363m), (filledTarget.Status, filledTarget.FilledQuantity, filledTarget.Fee, filledTarget.RealizedPnl));

        // The remainder's stop fills: the trade closes with its outcome, the leftover target leg is cancelled, and the same event again changes nothing.
        var stopped = await account.FillAsync(remainderStop.ClientOrderId, 94.52m, fee: 0.00633m);
        await Wait.UntilAsync(() => ClosedAt(agent.Id) is not null, "closed");
        await account.PushAsync(new UserDataEvent.OrderUpdate(account.KeyId, MarketType.Spot, account.Now, stopped, ExecutionType.Trade, account.Trades[^1]));
        await Wait.SettleAsync(200);
        trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((TradeExitReason.Stop, 0m), (trade.ExitReason, trade.RemainingSize));
        Assert.Equal(-0.01m + 10m * 0.033m - 0.00363m + (94.52m - 100m) * 0.067m - 0.00633m, trade.RealizedPnl);
        Assert.Equal(0.01m + 0.00363m + 0.00633m, trade.Fees);
        Assert.Empty((await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id)).Orders);
        Assert.Equal(2, (await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id)).Count(o => o.Purpose != OrderPurpose.Entry && o.Status == OrderStatus.Filled));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AgentsLiveSeed.ExchangeSecret));

        await tracker.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task ATriggeredFuturesStopClosesTheTradeWithTheFillsFeeAndTheFundingAndASmallLimitPausesTheAgent()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "fills-futures@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.Agents.SingleAsync(a => a.Id == agent.Id, Ct);
            row.RiskLimits = new RiskLimits { DailyLossLimitPercent = 0.005m };
            await db.SaveChangesAsync(Ct);
        }
        var account = ExecutionLiveSeed.Account(exchange, agent);
        account.Funding.Add(new FundingPayment("BTCUSDT", -0.5m, "USDT", account.Now, "f-1"));
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var tracker = provider.GetRequiredService<LiveFillTracker>();
        await tracker.StartAsync(Ct);
        var events = new List<AgentEvent>();
        using var subscription = provider.GetRequiredService<IAgentEvents>().Subscribe(user.Id, e => { lock (events) events.Add(e); return ValueTask.CompletedTask; });

        await venue.PlaceEntryAsync(agent, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Market, null, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
        await Wait.UntilAsync(() => account.SubscriberCount == 1, "subscribed");
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        var stop = open.Single(o => o.Purpose == OrderPurpose.StopLoss);
        Assert.NotNull(stop.ExchangeAlgoId);

        await account.TriggerAsync(stop.ClientOrderId, 95m, fee: 0.0095m, realizedPnl: -0.5m);
        await Wait.UntilAsync(() => ClosedAt(agent.Id) is not null, "closed");
        await Wait.UntilAsync(() => account.CancelledConditional.Count == 2, "leftover targets cancelled");

        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((TradeExitReason.Stop, 0m, -0.5m), (trade.ExitReason, trade.RemainingSize, trade.Funding));
        Assert.Equal(-0.01m - 0.5m - 0.0095m - 0.5m, trade.RealizedPnl);
        Assert.Equal(0.01m + 0.0095m, trade.Fees);
        var rows = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        var filledStop = rows.Single(o => o.Id == stop.Id);
        Assert.Equal((OrderStatus.Filled, 0.1m, 95m), (filledStop.Status, filledStop.FilledQuantity, filledStop.AverageFillPrice));
        Assert.NotNull(filledStop.ExchangeOrderId);
        Assert.All(rows.Where(o => o.Purpose == OrderPurpose.TakeProfit), o => Assert.Equal(OrderStatus.Canceled, o.Status));
        Assert.Equal(2, account.CancelledConditional.Count);

        // A loss of about 1 on a 10,000 account is over a 0.005% limit: paused until the next UTC day (FR-EXE-09).
        await Wait.UntilAsync(() => events.OfType<AgentStateChanged>().Any(e => e.State == AgentState.Paused), "paused");
        var paused = await ExecutionLiveSeed.AgentAsync(_fixture, agent.Id);
        Assert.Equal((AgentState.Paused, PauseCause.DailyLoss), (paused.State, paused.PauseCause));

        await tracker.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private decimal? RemainingSize(Guid agentId)
    {
        using var db = _fixture.CreateContext();
        return db.Trades.AsNoTracking().Where(t => t.AgentId == agentId).Select(t => (decimal?)t.RemainingSize).FirstOrDefault();
    }

    private DateTimeOffset? ClosedAt(Guid agentId)
    {
        using var db = _fixture.CreateContext();
        return db.Trades.AsNoTracking().Where(t => t.AgentId == agentId).Select(t => t.ClosedAt).FirstOrDefault();
    }

    [Fact]
    public async Task ASpotStopLegClosesItsUnitOnlyAndTheLastLegClosesTheTrade()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "fills-legs@example.com");
        var (provider, _, _, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        account.FillMarketOrders = false;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var tracker = provider.GetRequiredService<LiveFillTracker>();
        await tracker.StartAsync(Ct);
        var decision = await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        var entry = await venue.PlaceEntryAsync(agent, new EntryPlacement(decision.Id, PositionSide.Long, OrderType.Limit, 100m, 0.1m, 95m, [110m, 130m], 0.33m, ExecutionLiveSeed.LastClose.AddHours(1)), Ct);
        await Wait.UntilAsync(() => account.SubscriberCount == 1, "subscribed");
        await account.FillAsync(entry.ClientOrderId, 100m, fee: 0.01m);
        await Wait.UntilAsync(() => account.PlacedOcos.Count == 2, "protected");
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        var partialStop = open.Single(o => o.Purpose == OrderPurpose.StopLoss && o.Quantity == 0.033m);
        var remainderStop = open.Single(o => o.Purpose == OrderPurpose.StopLoss && o.Quantity == 0.067m);

        // Two OCOs mean two stop legs: the partial unit's leg fills first and takes only its unit; the remainder stays open and protected.
        await account.FillAsync(partialStop.ClientOrderId, 94.5m, fee: 0.003m);
        await Wait.UntilAsync(() => RemainingSize(agent.Id) == 0.067m, "reduced by the stop leg");
        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Null(trade.ClosedAt);
        Assert.Equal(-0.01m + (94.5m - 100m) * 0.033m - 0.003m, trade.RealizedPnl);
        var (_, stillOpen) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        Assert.Contains(stillOpen, o => o.Id == remainderStop.Id);
        Assert.Contains(stillOpen, o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 130m);
        Assert.Empty(account.CancelledOcos);

        // The remainder's leg fills: now the trade is closed by its stop.
        await account.FillAsync(remainderStop.ClientOrderId, 94.4m, fee: 0.006m);
        await Wait.UntilAsync(() => ClosedAt(agent.Id) is not null, "closed");
        await Wait.SettleAsync(200);
        trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((TradeExitReason.Stop, 0m), (trade.ExitReason, trade.RemainingSize));
        Assert.Equal(-0.01m + (94.5m - 100m) * 0.033m - 0.003m + (94.4m - 100m) * 0.067m - 0.006m, trade.RealizedPnl);
        Assert.Equal(0.01m + 0.003m + 0.006m, trade.Fees);
        Assert.Empty((await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id)).Orders);

        await tracker.StopAsync(Ct);
        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
