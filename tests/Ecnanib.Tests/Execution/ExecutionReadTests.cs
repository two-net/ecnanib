using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-AGT-11, FR-UI-03, FR-UI-05, FR-EXP-08 and NFR-SEC-04: what an agent's overview and chart read from Execution. The
/// position comes from the records alone, with no request to Binance. The journal's totals are net of fees and funding,
/// and an open trade's are read as it goes, once, so its unrealized P&amp;L can be too, and settling replaces them
/// rather than adding to them. Every read takes the user and keeps Demo and Live apart, and every change is told to
/// observers after it commits.
/// </summary>
public sealed class ExecutionReadTests(PostgresFixture postgres)
{
    private const decimal Price = ExecutionApp.Price;

    private static CancellationToken Cancellation => ExecutionApp.Cancellation;

    [Fact]
    public async Task The_position_is_read_from_the_records_alone_and_only_by_its_user()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        var positions = Positions(app);

        var requests = app.Agents.Binance.Requests.Count;
        var read = await positions.FindAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(requests, app.Agents.Binance.Requests.Count);
        Assert.Equal(new OpenPosition(PositionSide.Long, placed.Quantity, Price, null, ExecutionApp.Stop, ExecutionApp.Target), read.Position);
        Assert.Equal([ExecutionApp.Target], read.Targets);
        Assert.Equal((Market.Futures, TradingEnvironment.Demo, "BTCUSDT"), (read.Market!.Value, read.Environment!.Value, read.Symbol));
        Assert.NotNull(read.OpenedAt);
        Assert.Null(read.PendingEntry);
        Assert.Equal(Math.Round(100m * placed.Quantity, 8), read.UnrealizedPnl(Price + 100m));
        Assert.Null(read.UnrealizedPnl(null));

        Assert.Same(RecordedPosition.None, await positions.FindAsync(Guid.NewGuid(), agent.Id, Cancellation));

        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();

        Assert.Same(RecordedPosition.None, await positions.FindAsync(user.Id, agent.Id, Cancellation));
    }

    [Fact]
    public async Task An_open_trade_reads_its_entry_fees_once_and_its_funding_after_each_boundary_and_settling_replaces_both()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, target: false)));
        var placed = app.FuturesLong();
        var q = placed.Quantity;
        var entryFee = q * Price * FakeOrderBook.FuturesFee;

        // Paused, so no cycle runs while the clock moves on: what it holds is still the journal's.
        await app.SetStatusAsync(agent.Id, AgentStatus.Paused);
        await app.JournalAsync();

        var open = Assert.Single(await Journal(app).ReadTotalsAsync(user.Id, agent.Id, TradingEnvironment.Demo, Cancellation));
        Assert.Equal(("USDT", 0m, 0, 1, entryFee, 0m), (open.QuoteAsset, open.RealizedPnl, open.ClosedTrades, open.OpenTrades, open.OpenFees, open.OpenFunding));
        Assert.Empty(app.Agents.Binance.To(FakeOrderBook.FuturesIncome));

        var fills = app.Agents.Binance.To(FakeOrderBook.FuturesTrades).Count;
        await app.JournalAsync();
        Assert.Equal(fills, app.Agents.Binance.To(FakeOrderBook.FuturesTrades).Count);

        // A payment a minute later, then past the next four-hour boundary and every agent's share of the spread.
        var now = app.Clock.GetUtcNow();
        app.Book.Funding.Add((now.AddMinutes(1), -2m, 0.0001m));
        var boundary = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.FromHours(4).Ticks), TimeSpan.Zero).AddHours(4);
        app.Clock.Advance(boundary - now + TimeSpan.FromMinutes(40));
        await app.JournalAsync();

        open = Assert.Single(await Journal(app).ReadTotalsAsync(user.Id, agent.Id, TradingEnvironment.Demo, Cancellation));
        Assert.Equal((entryFee, -2m), (open.OpenFees, open.OpenFunding));

        var incomes = app.Agents.Binance.To(FakeOrderBook.FuturesIncome).Count;
        await app.JournalAsync();
        Assert.Equal(incomes, app.Agents.Binance.To(FakeOrderBook.FuturesIncome).Count);

        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();
        await app.JournalAsync();

        // Read again, whole, with nothing counted twice.
        var fees = entryFee + (q * ExecutionApp.Stop * FakeOrderBook.FuturesFee);
        var realized = (-200m * q) - fees - 2m;
        Assert.Equal(2, (await app.FillsAsync(agent.Id)).Count);
        var settled = Assert.Single(await Journal(app).ReadTotalsAsync(user.Id, agent.Id, TradingEnvironment.Demo, Cancellation));
        Assert.Equal(("USDT", realized, 1, 0, 0, 0, 0m, 0m), Tuple(settled));
        var trade = Assert.Single(await Journal(app).ListTradesAsync(user.Id, agent.Id, new TradeQuery(TradingEnvironment.Demo), Cancellation));
        Assert.Equal((fees, -2m, realized, true), (trade.Fees, trade.Funding, trade.RealizedPnl, trade.Settled));
    }

    [Fact]
    public async Task The_totals_and_the_trades_keep_Demo_and_Live_apart_and_count_a_trade_not_yet_settled()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();
        var journal = Journal(app);

        // Booked from the orders, before any fee is read, so it's gross and provisional.
        var demo = Assert.Single(await journal.ReadTotalsAsync(user.Id, agent.Id, TradingEnvironment.Demo, Cancellation));
        Assert.Equal(("USDT", -200m * placed.Quantity, 1, 1, 0, 0, 0m, 0m), Tuple(demo));
        Assert.Empty(await journal.ReadTotalsAsync(user.Id, agent.Id, TradingEnvironment.Live, Cancellation));
        Assert.Empty(await journal.ReadTotalsAsync(Guid.NewGuid(), agent.Id, TradingEnvironment.Demo, Cancellation));

        var trade = Assert.Single(await journal.ListTradesAsync(user.Id, agent.Id, new TradeQuery(TradingEnvironment.Demo), Cancellation));
        Assert.Equal(
            (TradeStatus.Closed, (TradeExit?)TradeExit.StopFilled, PositionSide.Long, false, "BTCUSDT", TradingEnvironment.Demo, Price, (decimal?)ExecutionApp.Stop),
            (trade.Status, trade.ExitReason, trade.Side, trade.Settled, trade.Symbol, trade.Environment, trade.EntryPrice, trade.ExitPrice));

        var (opened, closed) = (trade.OpenedAt, trade.ClosedAt!.Value);
        Assert.Single(await journal.ListTradesAsync(user.Id, agent.Id, new TradeQuery(TradingEnvironment.Demo, "BTCUSDT", opened, closed), Cancellation));
        Assert.Empty(await journal.ListTradesAsync(user.Id, agent.Id, new TradeQuery(TradingEnvironment.Demo, From: closed.AddMinutes(1)), Cancellation));
        Assert.Empty(await journal.ListTradesAsync(user.Id, agent.Id, new TradeQuery(TradingEnvironment.Demo, To: opened.AddMinutes(-1)), Cancellation));
        Assert.Empty(await journal.ListTradesAsync(user.Id, agent.Id, new TradeQuery(TradingEnvironment.Demo, "ETHUSDT"), Cancellation));
        Assert.Empty(await journal.ListTradesAsync(user.Id, agent.Id, new TradeQuery(TradingEnvironment.Live), Cancellation));
        Assert.Empty(await journal.ListTradesAsync(Guid.NewGuid(), agent.Id, new TradeQuery(TradingEnvironment.Demo), Cancellation));
    }

    [Fact]
    public async Task Each_change_to_an_agents_orders_and_trades_is_told_after_it_commits()
    {
        var observer = new ExecutionRecorder();
        await using var app = new ExecutionApp(postgres, services: services => services.AddSingleton<IExecutionObserver>(observer));
        var (user, agent) = await app.AgentAsync(Market.Futures);
        observer.Read = () => Positions(app).FindAsync(user.Id, agent.Id, CancellationToken.None).GetAwaiter().GetResult().Position?.Quantity;

        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();

        Assert.NotEmpty(observer.Heard);
        Assert.All(observer.Heard, heard => Assert.Equal((user.Id, agent.Id), (heard.UserId, heard.AgentId)));

        // The last one told saw the position the commit made.
        Assert.Equal(placed.Quantity, observer.Heard[^1].Held);

        var entered = observer.Heard.Count;
        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();

        Assert.True(observer.Heard.Count > entered);
        Assert.Null(observer.Heard[^1].Held);

        var closed = observer.Heard.Count;
        await app.JournalAsync();
        Assert.True(observer.Heard.Count > closed);

        // Nothing left to book, settle or read, so nothing to tell.
        var settled = observer.Heard.Count;
        await app.JournalAsync();
        Assert.Equal(settled, observer.Heard.Count);
    }

    private static IAgentPositions Positions(ExecutionApp app) => app.Agents.App.Services.GetRequiredService<IAgentPositions>();

    private static ITradeJournal Journal(ExecutionApp app) => app.Agents.App.Services.GetRequiredService<ITradeJournal>();

    private static (string, decimal, int, int, int, int, decimal, decimal) Tuple(JournalTotals totals) =>
        (totals.QuoteAsset, totals.RealizedPnl, totals.ClosedTrades, totals.Provisional, totals.FeesUnknown, totals.OpenTrades, totals.OpenFees, totals.OpenFunding);

    private sealed class ExecutionRecorder : IExecutionObserver
    {
        private readonly ConcurrentQueue<(Guid UserId, Guid AgentId, decimal? Held)> _heard = new();

        /// <summary>What the agent holds by its records when it's told, which another connection reads.</summary>
        public Func<decimal?> Read { get; set; } = () => null;

        public IReadOnlyList<(Guid UserId, Guid AgentId, decimal? Held)> Heard => [.. _heard];

        public void OnChanged(Guid userId, Guid agentId) => _heard.Enqueue((userId, agentId, Read()));
    }
}
