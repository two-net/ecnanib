using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Abstractions;
using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution.Live;

/// <summary>The trade lifecycle timeline (FR-UI-03 e) against PostgreSQL: evaluation, decision, entry, protection, stop moves, management, exit and close in order, for the owner only.</summary>
[Trait("Category", "Live")]
public class TradeTimelineReaderLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public TradeTimelineReaderLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => ExecutionLiveSeed.Ct;

    [Fact]
    public async Task ATradesLifeIsListedInOrderFromEvaluationToClose()
    {
        await StartAsync();
        var user = await AgentsLiveSeed.NewUserAsync(_fixture, "timeline@example.com");
        var other = await AgentsLiveSeed.NewUserAsync(_fixture, "timeline-other@example.com");
        var agent = await AgentsLiveSeed.NewAgentAsync(_fixture, user.Id, "BTC", AgentState.Running);
        var decision = await AgentsLiveSeed.NewDecisionAsync(_fixture, agent, ExecutionLiveSeed.LastOpen);
        var trade = await AgentsLiveSeed.NewOpenTradeAsync(_fixture, agent, decision);
        var now = AgentsLiveSeed.Now;
        var (provider, _, _, _, _) = ExecutionLiveSeed.Build(_fixture);
        await using var _ = provider;
        var reader = provider.GetRequiredService<ITradeTimelineReader>();

        await using (var db = _fixture.CreateContext())
        {
            db.Add(new Order
            {
                AgentId = agent.Id, DecisionId = decision.Id, TradeId = trade.Id, ClientOrderId = "tl-entry-" + Guid.NewGuid().ToString("N")[..16], Purpose = OrderPurpose.Entry, Type = OrderType.Market,
                Side = OrderSide.Buy, Quantity = 0.1m, FilledQuantity = 0.1m, AverageFillPrice = 100m, Status = OrderStatus.Filled, IsPaper = true, CreatedAt = now.AddSeconds(-3), UpdatedAt = now.AddSeconds(-1), FilledAt = now.AddSeconds(-1),
            });
            await db.SaveChangesAsync(Ct);
        }

        var open = await reader.GetAsync(user.Id, trade.Id, Ct);

        Assert.NotNull(open);
        Assert.True(open.IsOpen);
        Assert.Equal([TimelineKind.Evaluated, TimelineKind.Decided, TimelineKind.EntryPlaced, TimelineKind.EntryFilled, TimelineKind.Protected], open.Events.Select(e => e.Kind));
        Assert.Equal(decision.EvaluationId, open.Events[0].EvaluationId);
        Assert.Equal(DecisionAction.OpenLong, open.Events[1].Action);
        Assert.Equal(100m, open.Events[3].Price);
        Assert.Equal(0.1m, open.Events[3].Quantity);
        Assert.Equal(90m, open.Events[4].Price);
        Assert.Equal([120m], open.Events[4].Targets);
        Assert.Equal("initial", open.Events[4].Reason);

        // A breakeven stop move, a managing decision that changes the targets, then the target fills and the trade closes.
        await using (var db = _fixture.CreateContext())
        {
            var row = await db.Trades.SingleAsync(t => t.Id == trade.Id, Ct);
            row.StopHistory = TradeJson.AppendStop(row.StopHistory, now.AddHours(1), 100m, "breakeven");
            row.CurrentStop = 100m;
            var evaluation = new Evaluation { AgentId = agent.Id, Symbol = agent.Symbol, MarketType = agent.MarketType, Timeframe = agent.Timeframe, CandleOpenTime = ExecutionLiveSeed.LastOpen.AddHours(1), StartedAt = now.AddHours(1).AddSeconds(2), FinishedAt = now.AddHours(1).AddSeconds(9), CouncilConsulted = true, Outcome = EvaluationOutcome.Decided };
            db.Add(new Decision { Evaluation = evaluation, TradeId = trade.Id, FinalAction = DecisionAction.Modify, Targets = JsonSerializer.SerializeToElement(new[] { 125m }), Rationale = "Tighten.", CreatedAt = now.AddHours(1).AddSeconds(5) });
            var target = await db.Orders.SingleAsync(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.TakeProfit, Ct);
            target.Status = OrderStatus.Filled;
            target.FilledQuantity = 0.1m;
            target.AverageFillPrice = 125m;
            target.FilledAt = now.AddHours(2);
            target.UpdatedAt = now.AddHours(2);
            target.RealizedPnl = 2.4m;
            row.RemainingSize = 0m;
            row.ExitPrice = 125m;
            row.RealizedPnl = 2.4m;
            row.ExitReason = TradeExitReason.Target;
            row.ClosedAt = now.AddHours(2);
            await db.SaveChangesAsync(Ct);
        }

        var closed = Assert.Single(await reader.ListRecentAsync(user.Id, agent.Id, 5, Ct));

        Assert.False(closed.IsOpen);
        Assert.Equal(2.4m, closed.RealizedPnl);
        Assert.Equal(
            [TimelineKind.Evaluated, TimelineKind.Decided, TimelineKind.EntryPlaced, TimelineKind.EntryFilled, TimelineKind.Protected, TimelineKind.StopMoved, TimelineKind.Managed, TimelineKind.TargetsChanged, TimelineKind.Exit, TimelineKind.Closed],
            closed.Events.Select(e => e.Kind));
        Assert.Equal(("breakeven", 100m), (closed.Events[5].Reason, closed.Events[5].Price));
        Assert.Equal(DecisionAction.Modify, closed.Events[6].Action);
        Assert.Equal([125m], closed.Events[7].Targets);
        Assert.Equal((125m, 2.4m, OrderPurpose.TakeProfit), (closed.Events[8].Price, closed.Events[8].RealizedPnl, closed.Events[8].Purpose));
        Assert.Equal((TradeExitReason.Target, 2.4m), (closed.Events[9].ExitReason, closed.Events[9].RealizedPnl));
        Assert.True(closed.Events.Zip(closed.Events.Skip(1)).All(pair => pair.First.At <= pair.Second.At));

        Assert.Null(await reader.GetAsync(other.Id, trade.Id, Ct));
        Assert.Empty(await reader.ListRecentAsync(other.Id, agent.Id, 5, Ct));
        Assert.Null(await reader.GetAsync(user.Id, Guid.NewGuid(), Ct));

        await AgentsLiveSeed.DeleteUsersAsync(_fixture, user.Id, other.Id);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
