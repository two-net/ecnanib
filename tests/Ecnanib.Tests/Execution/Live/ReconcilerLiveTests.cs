using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Services.Live;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>FR-EXE-08: the exchange is the truth, every adoption a warning.</summary>
[Trait("Category", "Live")]
public class ReconcilerLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public ReconcilerLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    [Fact]
    public async Task AnOrderFilledWhileNobodyListenedBecomesATradeOnTheNextRoundAndAYoungRowWaits()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "recon-fill@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        account.FillMarketOrders = false;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var reconciler = provider.GetRequiredService<ExchangeReconciler>();
        var entry = await venue.PlaceEntryAsync(agent, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Limit, 100m, 0.1m, 95m, [110m], 0.5m, null), Ct);
        await account.FillAsync(entry.ClientOrderId, 100m);

        await reconciler.RoundAsync(Ct);
        Assert.Null(await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id));

        time.Advance(TimeSpan.FromMinutes(1));
        await reconciler.RoundAsync(Ct);

        var trade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, agent.Id))!;
        Assert.Equal((100m, 0.1m), (trade.EntryPrice, trade.RemainingSize));
        // The units come from the decision's targets ([110, 130] seeded), one OCO each.
        Assert.Equal(2, account.PlacedOcos.Count);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("is Filled on the exchange") && e.Message.Contains("the exchange state is adopted"));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AVanishedFuturesPositionClosesTheTradeAsExternalAndASmallerOneShrinksItAndResizesTheStop()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "recon-position@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var reconciler = provider.GetRequiredService<ExchangeReconciler>();
        var gone = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "gone", MarketType.UsdFutures);
        var smaller = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "smaller", MarketType.UsdFutures, "ETHUSDT");
        var goneBook = ExecutionLiveSeed.Account(exchange, gone);
        var smallerBook = ExecutionLiveSeed.Account(exchange, smaller);
        await venue.PlaceEntryAsync(gone, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, gone, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Market, null, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
        await venue.PlaceEntryAsync(smaller, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, smaller, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Market, null, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
        goneBook.SetPosition("BTCUSDT", 0m, 100m);
        goneBook.Trades.Add(new UserTrade("BTCUSDT", 777, 777, 103m, 0.1m, 10.3m, 0.0103m, "USDT", false, false, 0.3m, goneBook.Now));
        goneBook.Funding.Add(new FundingPayment("BTCUSDT", -0.2m, "USDT", goneBook.Now, "f-2"));
        smallerBook.SetPosition("ETHUSDT", 0.04m, 100m);
        time.Advance(TimeSpan.FromMinutes(1));

        await reconciler.RoundAsync(Ct);

        var external = (await ExecutionLiveSeed.TradeOfAsync(_fixture, gone.Id))!;
        Assert.Equal((TradeExitReason.External, 0m, 103m, -0.2m), (external.ExitReason, external.RemainingSize, external.ExitPrice, external.Funding));
        Assert.Equal(-0.01m + 0.3m - 0.0103m - 0.2m, external.RealizedPnl);
        Assert.NotNull(external.ClosedAt);
        Assert.Empty((await ExecutionLiveSeed.OpenAsync(_fixture, gone.Id)).Orders);
        Assert.Equal(3, goneBook.CancelledConditional.Count);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("was closed on the exchange outside the system"));

        var shrunk = (await ExecutionLiveSeed.TradeOfAsync(_fixture, smaller.Id))!;
        Assert.Equal(0.04m, shrunk.RemainingSize);
        Assert.Null(shrunk.ClosedAt);
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, smaller.Id);
        Assert.Equal(0.04m, Assert.Single(open, o => o.Purpose == OrderPurpose.StopLoss).Quantity);
        Assert.Single(smallerBook.CancelledConditional);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("the exchange size is adopted"));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AnOrderUnknownToTheExchangeIsMarkedAMissingStopIsReplacedAndABusyAgentWaits()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "recon-stop@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var reconciler = provider.GetRequiredService<ExchangeReconciler>();
        var agent = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var account = ExecutionLiveSeed.Account(exchange, agent);
        await venue.PlaceEntryAsync(agent, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Market, null, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
        account.SetPosition("BTCUSDT", 0.1m, 100m);
        var (_, open) = await ExecutionLiveSeed.OpenAsync(_fixture, agent.Id);
        var stop = open.Single(o => o.Purpose == OrderPurpose.StopLoss);
        var target = open.First(o => o.Purpose == OrderPurpose.TakeProfit);
        account.CancelConditional(OrderRef.ByClientId(stop.ClientOrderId));
        account.Conditional.RemoveAll(o => o.ClientAlgoId == target.ClientOrderId);
        time.Advance(TimeSpan.FromMinutes(1));

        // Under the agent's gate the round leaves it alone; the next round adopts the exchange state.
        using (await provider.GetRequiredService<IAgentGates>().LockAsync(agent.Id, Ct))
        {
            await reconciler.RoundAsync(Ct);
            Assert.Equal(OrderStatus.New, (await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id)).Single(o => o.Id == stop.Id).Status);
        }

        await reconciler.RoundAsync(Ct);

        var rows = await ExecutionLiveSeed.OrdersOfAsync(_fixture, agent.Id);
        Assert.Equal(OrderStatus.Canceled, rows.Single(o => o.Id == stop.Id).Status);
        Assert.Equal(OrderStatus.Canceled, rows.Single(o => o.Id == target.Id).Status);
        var replaced = Assert.Single(rows, o => o.Purpose == OrderPurpose.StopLoss && o.Status == OrderStatus.New);
        Assert.Equal((95m, 0.1m), (replaced.StopPrice, replaced.Quantity));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("is unknown to the exchange"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("has no stop on the exchange"));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    [Fact]
    public async Task AnExitInFlightIsNotAdoptedAsExternalAndASpotRemainderIsProtectedAgainAtItsFinalTarget()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "recon-pending@example.com");
        var (provider, logs, time, exchange, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var venue = provider.GetRequiredService<IOrderVenues>().For(AgentMode.Live);
        var reconciler = provider.GetRequiredService<ExchangeReconciler>();
        var futures = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "futures", MarketType.UsdFutures);
        var spot = await ExecutionLiveSeed.LiveAgentAsync(_fixture, provider, user.Id, "spot", MarketType.Spot, "ETHUSDT");
        var futuresBook = ExecutionLiveSeed.Account(exchange, futures);
        var spotBook = ExecutionLiveSeed.Account(exchange, spot);
        await venue.PlaceEntryAsync(futures, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, futures, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Market, null, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);
        await venue.PlaceEntryAsync(spot, new EntryPlacement((await ExecutionLiveSeed.NewDecisionAsync(_fixture, spot, ExecutionLiveSeed.LastOpen)).Id, PositionSide.Long, OrderType.Market, null, 0.1m, 95m, [110m, 130m], 0.33m, null), Ct);

        // Futures: a partial close of ours is still pending while the exchange already shows the smaller position; that fill is on its way and is not adopted too.
        futuresBook.FillMarketOrders = false;
        var futuresTrade = (await ExecutionLiveSeed.TradeOfAsync(_fixture, futures.Id))!;
        var partial = await venue.PlacePartialCloseAsync(futures, futuresTrade, 0.033m, futuresTrade.EntryDecisionId, Ct);
        futuresBook.SetPosition("BTCUSDT", 0.067m, 100m);

        // Spot: the first target filled while nobody listened and the remaining unit's OCO was cancelled on the exchange, so the remainder has no stop.
        var (_, spotOpen) = await ExecutionLiveSeed.OpenAsync(_fixture, spot.Id);
        var firstTarget = spotOpen.Single(o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 110m);
        var remainderTarget = spotOpen.Single(o => o.Purpose == OrderPurpose.TakeProfit && o.Price == 130m);
        await spotBook.FillAsync(firstTarget.ClientOrderId, 110m, fee: 0.00363m);
        spotBook.CancelOco("ETHUSDT", remainderTarget.ListClientOrderId!);
        spotBook.Balances.Add(new AssetBalance("ETH", 0.067m, 0m, 0.067m, null));
        time.Advance(TimeSpan.FromMinutes(1));

        await reconciler.RoundAsync(Ct);

        var kept = (await ExecutionLiveSeed.TradeOfAsync(_fixture, futures.Id))!;
        Assert.Equal(0.1m, kept.RemainingSize);
        Assert.Equal(OrderStatus.New, (await ExecutionLiveSeed.OrdersOfAsync(_fixture, futures.Id)).Single(o => o.Id == partial.Id).Status);
        Assert.Equal(0.1m, Assert.Single((await ExecutionLiveSeed.OpenAsync(_fixture, futures.Id)).Orders, o => o.Purpose == OrderPurpose.StopLoss).Quantity);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("the exchange size is adopted"));

        // The remainder is one unit at the final target, not a fresh split that would put a take-profit at the target already reached.
        var reduced = (await ExecutionLiveSeed.TradeOfAsync(_fixture, spot.Id))!;
        Assert.Equal(0.067m, reduced.RemainingSize);
        Assert.Null(reduced.ClosedAt);
        var (_, reprotected) = await ExecutionLiveSeed.OpenAsync(_fixture, spot.Id);
        Assert.Equal((0.067m, 95m), (Assert.Single(reprotected, o => o.Purpose == OrderPurpose.StopLoss).Quantity, Assert.Single(reprotected, o => o.Purpose == OrderPurpose.StopLoss).StopPrice));
        Assert.Equal((0.067m, 130m), (Assert.Single(reprotected, o => o.Purpose == OrderPurpose.TakeProfit).Quantity, Assert.Single(reprotected, o => o.Purpose == OrderPurpose.TakeProfit).Price));
        Assert.Equal(3, spotBook.PlacedOcos.Count);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("has no stop on the exchange"));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
