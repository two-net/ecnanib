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
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CandleInterval.OneHour, side, type, limit, 1m, stop, target is { } t ? [t] : [], 1m, expiresAt);

    /// <summary>A one-unit position: the whole size at one target, as a position too small to split is protected.</summary>
    private static PaperPosition Position(PositionSide side, decimal entry = 100m, decimal? stop = 90m, decimal? target = 120m)
    {
        var position = new PaperPosition
        {
            TradeId = Guid.NewGuid(), AgentId = Guid.NewGuid(), UserId = Guid.NewGuid(), Side = side, EntryPrice = entry, Size = 1m, RemainingSize = 1m,
            InitialStop = stop, Stop = stop, StopOrderId = stop is null ? null : Guid.NewGuid(), HighestPrice = entry, LowestPrice = entry,
        };
        position.Units.Add(new PaperUnit { Quantity = 1m, Target = target, TargetOrderId = target is null ? null : Guid.NewGuid() });
        return position;
    }

    /// <summary>Two units (Appendix A): the partial at the first target, the remainder at the second or trailing without one.</summary>
    private static PaperPosition TwoUnits(PositionSide side, decimal partial, decimal firstTarget, decimal? finalTarget, decimal entry = 100m, decimal stop = 90m)
    {
        var position = new PaperPosition
        {
            TradeId = Guid.NewGuid(), AgentId = Guid.NewGuid(), UserId = Guid.NewGuid(), Side = side, EntryPrice = entry, Size = 1m, RemainingSize = 1m,
            InitialStop = stop, Stop = stop, StopOrderId = Guid.NewGuid(), HighestPrice = entry, LowestPrice = entry,
        };
        position.Units.Add(new PaperUnit { Quantity = partial, Target = firstTarget, TargetOrderId = Guid.NewGuid() });
        position.Units.Add(new PaperUnit { Quantity = 1m - partial, Target = finalTarget, TargetOrderId = finalTarget is null ? null : Guid.NewGuid() });
        return position;
    }

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
        stopped.Units[0].Target = 95m;
        var tie = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(89m, At, Slippage)));
        Assert.Equal(TradeExitReason.Stop, tie.Reason);
        Assert.Equal(89.91m, tie.Price);
        Assert.Equal(stopped.StopOrderId, tie.OrderId);
        Assert.False(tie.IsMaker);

        book.AddPosition(targeted);
        var targetOrderId = targeted.Units[0].TargetOrderId;
        var hit = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(121m, At, Slippage)));
        Assert.Equal(TradeExitReason.Target, hit.Reason);
        Assert.Equal(120m, hit.Price);
        Assert.Equal(targetOrderId, hit.OrderId);
        Assert.Equal(1m, hit.Quantity);
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
        var mine = Position(PositionSide.Long) ;
        mine = new PaperPosition
        {
            TradeId = mine.TradeId, AgentId = agentId, UserId = Guid.NewGuid(), Side = PositionSide.Long, EntryPrice = 100m, Size = 1m, RemainingSize = 1m,
            Stop = 90m, StopOrderId = Guid.NewGuid(), CloseOrderId = Guid.NewGuid(), CloseReason = TradeExitReason.Council, PartialOrderId = Guid.NewGuid(), PartialQuantity = 0.5m,
        };
        mine.Units.Add(new PaperUnit { Quantity = 1m, Target = 120m, TargetOrderId = Guid.NewGuid() });
        book.AddEntry(entry);
        book.AddEntry(Entry(PositionSide.Long, OrderType.Limit, limit: 95m));
        book.AddPosition(position);
        book.AddPosition(mine);

        Assert.Equal(1, book.ClearAgent(agentId));

        Assert.Single(book.Entries);
        Assert.Null(mine.Stop);
        Assert.All(mine.Units, u => Assert.Null(u.Target));
        Assert.Null(mine.CloseOrderId);
        Assert.Null(mine.PartialOrderId);
        Assert.False(mine.IsClosing);
        Assert.Equal(90m, position.Stop);
        Assert.Empty(book.OnTick(50m, At, Slippage).OfType<PositionClosed>().Where(c => c.Position == mine));
    }

    [Fact]
    public void AUnitsTargetTakesThatUnitOffAndTheLastUnitClosesTheTrade()
    {
        var book = new PaperBook(MarketType.UsdFutures, "ETHUSDT");
        var position = TwoUnits(PositionSide.Long, partial: 0.4m, firstTarget: 110m, finalTarget: 130m);
        book.AddPosition(position);

        var reduced = Assert.IsType<PositionReduced>(Assert.Single(book.OnTick(111m, At, Slippage)));

        Assert.Equal(TradeExitReason.Target, reduced.Reason);
        Assert.Equal(0.4m, reduced.Quantity);
        Assert.Equal(110m, reduced.Price);
        Assert.True(reduced.IsMaker);
        Assert.Equal(0.6m, position.RemainingSize);
        Assert.Single(position.Units);
        Assert.Single(book.Positions);

        var closed = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(131m, At, Slippage)));
        Assert.Equal(TradeExitReason.Target, closed.Reason);
        Assert.Equal(0.6m, closed.Quantity);
        Assert.Equal(130m, closed.Price);
        Assert.True(book.IsEmpty);
    }

    [Fact]
    public void TheStopClosesWhateverIsLeftAfterAPartialAndARemainderWithoutTargetTrails()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        var position = TwoUnits(PositionSide.Long, partial: 0.33m, firstTarget: 110m, finalTarget: null);
        book.AddPosition(position);

        Assert.IsType<PositionReduced>(Assert.Single(book.OnTick(110m, At, Slippage)));
        Assert.Equal(0.67m, position.RemainingSize);
        Assert.Null(Assert.Single(position.Units).Target);
        Assert.Empty(book.OnTick(150m, At, Slippage));

        var closed = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(90m, At, Slippage)));
        Assert.Equal(TradeExitReason.Stop, closed.Reason);
        Assert.Equal(0.67m, closed.Quantity);
        Assert.Equal(89.91m, closed.Price);
    }

    [Fact]
    public void AMarkedPartialFillsAtTheNextTickOffTheTrailingRemainderFirst()
    {
        var book = new PaperBook(MarketType.Spot, "BTCUSDT");
        var position = TwoUnits(PositionSide.Long, partial: 0.4m, firstTarget: 110m, finalTarget: 130m);
        book.AddPosition(position);
        var partialOrder = Guid.NewGuid();
        position.PartialOrderId = partialOrder;
        position.PartialQuantity = 0.7m;

        var reduced = Assert.IsType<PositionReduced>(Assert.Single(book.OnTick(100m, At, Slippage)));

        Assert.Equal(TradeExitReason.Council, reduced.Reason);
        Assert.Equal(partialOrder, reduced.OrderId);
        Assert.Equal(0.7m, reduced.Quantity);
        Assert.Equal(99.9m, reduced.Price);
        Assert.Equal(0.3m, position.RemainingSize);
        var unit = Assert.Single(position.Units);
        Assert.Equal((0.3m, 110m), (unit.Quantity, unit.Target));
        Assert.Null(position.PartialOrderId);

        position.PartialOrderId = Guid.NewGuid();
        position.PartialQuantity = 1m;
        var closed = Assert.IsType<PositionClosed>(Assert.Single(book.OnTick(100m, At, Slippage)));
        Assert.Equal(0.3m, closed.Quantity);
        Assert.True(book.IsEmpty);
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
