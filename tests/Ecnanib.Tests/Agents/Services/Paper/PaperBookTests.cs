using Ecnanib.Agents.Services.Paper;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Agents.Services.Paper;

/// <summary>The fill rules of FR-AGT-11 on the in-memory book, and the deterministic client order ids of FR-EXE-07.</summary>
public class PaperBookTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
    private const decimal Slippage = 0.001m;

    private static PaperEntry Entry(PositionSide side, OrderType type, decimal? limit = null, decimal stop = 90m, decimal? target = 120m, DateTimeOffset? expiresAt = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CandleInterval.OneHour, side, type, limit, 1m, stop, target, expiresAt);

    private static PaperPosition Position(PositionSide side, decimal entry = 100m, decimal? stop = 90m, decimal? target = 120m) => new()
    {
        TradeId = Guid.NewGuid(), AgentId = Guid.NewGuid(), UserId = Guid.NewGuid(), Side = side, EntryPrice = entry, Size = 1m,
        InitialStop = stop, Stop = stop, StopOrderId = stop is null ? null : Guid.NewGuid(), Target = target, TargetOrderId = target is null ? null : Guid.NewGuid(),
        HighestPrice = entry, LowestPrice = entry,
    };

    [Fact]
    public void AMarketEntryFillsOnTheNextTickWithSlippageAgainstTheTrader()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        var buy = Entry(PositionSide.Long, OrderType.Market);
        var sell = Entry(PositionSide.Short, OrderType.Market, stop: 110m, target: 80m);
        book.AddEntry(buy);
        book.AddEntry(sell);

        var fills = book.OnTick(100m, At, Slippage);

        var bought = Assert.IsType<EntryFilled>(fills[0]);
        Assert.Same(buy, bought.Entry);
        Assert.Equal(100.1m, bought.Price);
        Assert.False(bought.IsMaker);
        var sold = Assert.IsType<EntryFilled>(fills[1]);
        Assert.Equal(99.9m, sold.Price);
        Assert.Empty(book.Entries);
        Assert.True(book.IsEmpty);
    }

    [Fact]
    public void ALimitEntryFillsAtItsPriceOnlyOnceTradedThrough()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        var buy = Entry(PositionSide.Long, OrderType.Limit, limit: 95m);
        var sell = Entry(PositionSide.Short, OrderType.Limit, limit: 105m, stop: 115m, target: 90m);
        book.AddEntry(buy);
        book.AddEntry(sell);

        Assert.Empty(book.OnTick(100m, At, Slippage));
        var buyFill = Assert.IsType<EntryFilled>(Assert.Single(book.OnTick(95m, At, Slippage)));
        Assert.Same(buy, buyFill.Entry);
        Assert.Equal(95m, buyFill.Price);
        Assert.True(buyFill.IsMaker);
        var sellFill = Assert.IsType<EntryFilled>(Assert.Single(book.OnTick(106m, At, Slippage)));
        Assert.Same(sell, sellFill.Entry);
        Assert.Equal(105m, sellFill.Price);
    }

    [Fact]
    public void AnEntryPastItsTimeoutExpiresInsteadOfFilling()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        var entry = Entry(PositionSide.Long, OrderType.Limit, limit: 95m, expiresAt: At);
        book.AddEntry(entry);

        var fill = Assert.IsType<EntryExpired>(Assert.Single(book.OnTick(90m, At, Slippage)));

        Assert.Same(entry, fill.Entry);
        Assert.Empty(book.Entries);
    }

    [Fact]
    public void StopsFillWithSlippageAndTargetsAtTheirPriceAndTheStopWinsATie()
    {
        var book = new PaperBook(MarketType.UsdFutures, "ETHUSDT");
        var stopped = Position(PositionSide.Long);
        var targeted = Position(PositionSide.Long);
        var shortStopped = Position(PositionSide.Short, stop: 110m, target: 80m);
        var shortTargeted = Position(PositionSide.Short, stop: 110m, target: 80m);
        book.AddPosition(stopped);
        book.AddPosition(targeted);
        book.AddPosition(shortStopped);
        book.AddPosition(shortTargeted);

        Assert.Empty(book.OnTick(100m, At, Slippage));
        Assert.Equal(4, book.Positions.Count);

        // A gap through both the stop (90) and the target (120) closes at the stop.
        book.RemovePosition(targeted.TradeId);
        book.RemovePosition(shortStopped.TradeId);
        book.RemovePosition(shortTargeted.TradeId);
        stopped.Target = 95m;
        var tie = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(89m, At, Slippage)));
        Assert.Equal(TradeExitReason.Stop, tie.Reason);
        Assert.Equal(89.91m, tie.Price);
        Assert.Equal(stopped.StopOrderId, tie.OrderId);
        Assert.False(tie.IsMaker);

        book.AddPosition(targeted);
        var hit = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(121m, At, Slippage)));
        Assert.Equal(TradeExitReason.Target, hit.Reason);
        Assert.Equal(120m, hit.Price);
        Assert.Equal(targeted.TargetOrderId, hit.OrderId);
        Assert.True(hit.IsMaker);

        book.AddPosition(shortStopped);
        var shortStop = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(110m, At, Slippage)));
        Assert.Equal(TradeExitReason.Stop, shortStop.Reason);
        Assert.Equal(110.11m, shortStop.Price);

        book.AddPosition(shortTargeted);
        var shortTarget = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(79m, At, Slippage)));
        Assert.Equal(TradeExitReason.Target, shortTarget.Reason);
        Assert.Equal(80m, shortTarget.Price);
        Assert.True(book.IsEmpty);
    }

    [Fact]
    public void AnEntryFilledOnATickMeetsItsStopOnlyFromTheNextTick()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        book.AddEntry(Entry(PositionSide.Long, OrderType.Market, stop: 90m));

        // The tick at 85 fills the market entry (there is no position yet); the simulator adds the position afterwards.
        var fills = book.OnTick(85m, At, Slippage);
        Assert.IsType<EntryFilled>(Assert.Single(fills));
        Assert.Empty(book.Positions);

        book.AddPosition(Position(PositionSide.Long, entry: 85.085m));
        var stop = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(85m, At.AddSeconds(1), Slippage)));
        Assert.Equal(TradeExitReason.Stop, stop.Reason);
    }

    [Fact]
    public void AMarkedCloseFillsAtTheNextTickWithItsReasonAndExcursionsAreTracked()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        var position = Position(PositionSide.Long);
        book.AddPosition(position);

        book.OnTick(110m, At, Slippage);
        book.OnTick(95m, At, Slippage);
        Assert.Equal(110m, position.HighestPrice);
        Assert.Equal(95m, position.LowestPrice);

        var closeOrderId = Guid.NewGuid();
        position.CloseOrderId = closeOrderId;
        position.CloseReason = TradeExitReason.KillSwitch;
        var closed = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(100m, At, Slippage)));

        Assert.Equal(TradeExitReason.KillSwitch, closed.Reason);
        Assert.Equal(closeOrderId, closed.OrderId);
        Assert.Equal(99.9m, closed.Price);
        Assert.True(book.IsEmpty);
    }

    [Fact]
    public void ClearAgentDropsItsEntriesAndStripsProtectionOffItsPositions()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        var agentId = Guid.NewGuid();
        var entry = Entry(PositionSide.Long, OrderType.Limit, limit: 95m) with { AgentId = agentId };
        var position = Position(PositionSide.Long);
        var mine = new PaperPosition
        {
            TradeId = Guid.NewGuid(), AgentId = agentId, UserId = Guid.NewGuid(), Side = PositionSide.Long, EntryPrice = 100m, Size = 1m,
            Stop = 90m, StopOrderId = Guid.NewGuid(), Target = 120m, TargetOrderId = Guid.NewGuid(), CloseOrderId = Guid.NewGuid(), CloseReason = TradeExitReason.Council,
        };
        book.AddEntry(entry);
        book.AddEntry(Entry(PositionSide.Long, OrderType.Limit, limit: 95m));
        book.AddPosition(position);
        book.AddPosition(mine);

        Assert.Equal(1, book.ClearAgent(agentId));

        Assert.Single(book.Entries);
        Assert.Null(mine.Stop);
        Assert.Null(mine.Target);
        Assert.Null(mine.CloseOrderId);
        Assert.False(mine.IsClosing);
        Assert.Equal(90m, position.Stop);
        Assert.Empty(book.OnTick(50m, At, Slippage).OfType<PositionClosed>().Where(c => c.Position == mine));
    }

    [Fact]
    public void ClientOrderIdsAreDeterministicDistinctAndValidForBinance()
    {
        var agent = Guid.NewGuid();
        var scope = Guid.NewGuid();

        var first = PaperClientOrderId.For(agent, scope, PaperClientOrderId.Entry, 0);
        var again = PaperClientOrderId.For(agent, scope, PaperClientOrderId.Entry, 0);
        var other = PaperClientOrderId.For(agent, scope, PaperClientOrderId.StopLoss, 0);
        var sequenced = PaperClientOrderId.For(agent, scope, PaperClientOrderId.Entry, 1);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(first, sequenced);
        Assert.Equal(32, first.Length);
        Assert.StartsWith("p", first, StringComparison.Ordinal);
        Assert.True(OrderRequest.IsValidClientOrderId(first));
    }
}
