using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>The dashboard's money figures (FR-UI-01) against PostgreSQL: the sums agree with the paper equity and the daily loss limit, and stay with the owner.</summary>
[Trait("Category", "Live")]
public class AgentPerformanceLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public AgentPerformanceLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    [Fact]
    public async Task RealizedTodayAndAllTimeFeesFundingCountsAndPaperEquityPerAgentForTheOwnerOnly()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "perf@example.com");
        var other = await AgentsLiveSeed.NewUserAsync(_fixture, "perf-other@example.com");
        var paper = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "paper", AgentState.Running);
        var live = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "live", AgentState.Stopped, AgentMode.Live, symbol: "ETHUSDT");
        var foreign = await AgentsLiveSeed.NewAgentAsync(_fixture, other.Id, "foreign", AgentState.Running);
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, paper, ExecutionLiveSeed.LastOpen);
        await AgentsLiveSeed.NewOpenTradeAsync(_fixture, paper, decision);
        var now = AgentsLiveSeed.Now;
        await using (var db = _fixture.CreateContext())
        {
            // Closed today: +9.6 net, with the entry fee paid today; closed yesterday: -2.
            var wonToday = new Trade { AgentId = paper.Id, EntryDecisionId = decision.Id, Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour, Side = PositionSide.Long, EntryPrice = 100m, ExitPrice = 110m, Size = 1m, RemainingSize = 0m, Fees = 0.3m, Funding = 0.1m, RealizedPnl = 9.6m, ExitReason = TradeExitReason.Target, IsPaper = true, OpenedAt = now.AddHours(-3), ClosedAt = now };
            var lostYesterday = new Trade { AgentId = paper.Id, EntryDecisionId = decision.Id, Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour, Side = PositionSide.Long, EntryPrice = 100m, ExitPrice = 98m, Size = 1m, RemainingSize = 0m, Fees = 0.2m, Funding = 0m, RealizedPnl = -2m, ExitReason = TradeExitReason.Stop, IsPaper = true, OpenedAt = now.AddDays(-2), ClosedAt = now.AddDays(-1) };
            var foreignTrade = new Trade { AgentId = foreign.Id, EntryDecisionId = decision.Id, Symbol = "BTCUSDT", MarketType = MarketType.Spot, Timeframe = CandleInterval.OneHour, Side = PositionSide.Long, EntryPrice = 100m, ExitPrice = 150m, Size = 1m, RemainingSize = 0m, RealizedPnl = 50m, IsPaper = true, OpenedAt = now.AddDays(-2), ClosedAt = now };
            db.AddRange(wonToday, lostYesterday, foreignTrade);
            await db.SaveChangesAsync(Ct);
            db.AddRange(
                // Entered and exited today (Now is five seconds past midnight UTC): the entry fee counts against today's realized P&L.
                Order(paper, decision, wonToday, OrderPurpose.Entry, now.AddSeconds(-3), fee: 0.1m),
                Order(paper, decision, wonToday, OrderPurpose.TakeProfit, now, realized: 9.6m),
                Order(paper, decision, lostYesterday, OrderPurpose.Entry, now.AddDays(-2), fee: 0.1m),
                Order(paper, decision, lostYesterday, OrderPurpose.StopLoss, now.AddDays(-1), realized: -2m));
            await db.SaveChangesAsync(Ct);
        }

        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var performance = provider.GetRequiredService<IAgentPerformance>();

        var figures = await performance.GetAsync(user.Id, [paper.Id, live.Id, foreign.Id], Ct);

        Assert.Equal(new[] { live.Id, paper.Id }.Order(), figures.Keys.Order());
        var pnl = figures[paper.Id];
        Assert.Equal(7.6m, pnl.RealizedAllTime);
        Assert.Equal(9.5m, pnl.RealizedToday);
        Assert.Equal(0.5m, pnl.Fees);
        Assert.Equal(0.1m, pnl.Funding);
        Assert.Equal(2, pnl.ClosedTrades);
        Assert.Equal(1, pnl.OpenPositions);
        Assert.Equal(10_007.6m, pnl.PaperEquity);
        var empty = figures[live.Id];
        Assert.Equal(new AgentPnl(live.Id, 0m, 0m, 0m, 0m, 0, 0, null), empty);
        Assert.Empty(await performance.GetAsync(user.Id, [], Ct));
        Assert.Empty(await performance.GetAsync(other.Id, [paper.Id, live.Id], Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id, other.Id);
    }

    private static Order Order(Agent agent, Decision decision, Trade trade, OrderPurpose purpose, DateTimeOffset filledAt, decimal? fee = null, decimal? realized = null) => new()
    {
        AgentId = agent.Id, DecisionId = decision.Id, TradeId = trade.Id, ClientOrderId = "perf-" + Guid.NewGuid().ToString("N")[..20], Purpose = purpose,
        Type = purpose == OrderPurpose.Entry ? OrderType.Market : OrderType.Limit, Side = purpose == OrderPurpose.Entry ? OrderSide.Buy : OrderSide.Sell,
        Quantity = trade.Size, FilledQuantity = trade.Size, AverageFillPrice = purpose == OrderPurpose.Entry ? trade.EntryPrice : trade.ExitPrice, Status = OrderStatus.Filled,
        IsPaper = true, CreatedAt = filledAt.AddSeconds(-1), UpdatedAt = filledAt, FilledAt = filledAt, Fee = fee, RealizedPnl = realized,
    };

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
