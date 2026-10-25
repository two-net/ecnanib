using Ecnanib.Components.Live;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Abstractions;

namespace Ecnanib.Tests.Components.Live;

public class LiveMessagesTests
{
    [Theory]
    [InlineData(0, "now")]
    [InlineData(-5, "now")]
    [InlineData(725, "in 12 min 05 s")]
    [InlineData(59, "in 0 min 59 s")]
    [InlineData(3720, "in 1 h 02 min")]
    [InlineData(100_800, "in 1 d 04 h")]
    public void TheCountdownToTheNextEvaluationReadsNaturally(int seconds, string expected) =>
        Assert.Equal(expected, LiveMessages.NextEvaluation(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(null, "waiting for a price")]
    [InlineData(64120.5, "64120.5")]
    [InlineData(0.00001234, "0.00001234")]
    [InlineData(100.0, "100")]
    public void PricesDropTrailingZeros(double? price, string expected) =>
        Assert.Equal(expected, LiveMessages.Price(price is { } p ? (decimal)p : null));

    [Fact]
    public void TheIndicatorTextsAreExact()
    {
        Assert.Equal("Evaluating…", LiveMessages.Evaluating);
        Assert.Equal("Today (UTC)", LiveMessages.TodayUtc);
        Assert.Equal("Verdict: Open long", LiveMessages.Verdict(DecisionAction.OpenLong));
        Assert.Equal("Verdict: none", LiveMessages.Verdict(null));
    }

    [Fact]
    public void ARoleSlotSaysWhereItIs()
    {
        var enabled = new RoleSlot(CouncilRole.MarketAnalyst, 1, true);
        var disabled = new RoleSlot(CouncilRole.SentimentFlow, 2, false);

        Assert.Equal("waiting", LiveMessages.RoleState(enabled, null));
        Assert.Equal("thinking…", LiveMessages.RoleState(enabled, new RoleStep(RoleStage.Thinking, null, null)));
        Assert.Equal("succeeded", LiveMessages.RoleState(enabled, new RoleStep(RoleStage.Done, RoleOutputStatus.Succeeded, DecisionAction.OpenLong)));
        Assert.Equal("timed out", LiveMessages.RoleState(enabled, new RoleStep(RoleStage.Done, RoleOutputStatus.TimedOut, null)));
        Assert.Equal("disabled", LiveMessages.RoleState(disabled, null));
        Assert.Equal("disabled", LiveMessages.RoleState(disabled, new RoleStep(RoleStage.Done, RoleOutputStatus.Disabled, null)));
    }

    [Fact]
    public void TimelineStepsAreTitledAndDetailedInWords()
    {
        var at = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(("Council decided: Open long", "limit at 100.5"), Both(new TimelineEvent(at, TimelineKind.Decided, 100.5m, Action: DecisionAction.OpenLong)));
        Assert.Equal(("Entry placed", "0.1, at market"), Both(new TimelineEvent(at, TimelineKind.EntryPlaced, null, 0.1m)));
        Assert.Equal(("Entry filled", "0.1, at 100"), Both(new TimelineEvent(at, TimelineKind.EntryFilled, 100m, 0.1m)));
        Assert.Equal(("Entry ended without a fill", "expired"), Both(new TimelineEvent(at, TimelineKind.EntryEnded, Status: OrderStatus.Expired)));
        Assert.Equal(("Protected", "stop 95, targets 108, 112"), Both(new TimelineEvent(at, TimelineKind.Protected, 95m, Targets: [108m, 112m])));
        Assert.Equal(("Stop moved", "to 100, (breakeven)"), Both(new TimelineEvent(at, TimelineKind.StopMoved, 100m, Reason: "breakeven")));
        Assert.Equal(("Council: Modify", "Tighten."), Both(new TimelineEvent(at, TimelineKind.Managed, Action: DecisionAction.Modify, Reason: "Tighten.")));
        Assert.Equal(("Targets changed", "to 125"), Both(new TimelineEvent(at, TimelineKind.TargetsChanged, Targets: [125m])));
        Assert.Equal(("Exit filled", "0.1, at 125, P&L 2.40"), Both(new TimelineEvent(at, TimelineKind.Exit, 125m, 0.1m, RealizedPnl: 2.4m, Purpose: OrderPurpose.TakeProfit)));
        Assert.Equal(("Closed: target", "at 125, P&L 2.40"), Both(new TimelineEvent(at, TimelineKind.Closed, 125m, ExitReason: TradeExitReason.Target, RealizedPnl: 2.4m)));
        Assert.Equal(("Evaluated", ""), Both(new TimelineEvent(at, TimelineKind.Evaluated, EvaluationId: Guid.NewGuid())));
    }

    private static (string Title, string Detail) Both(TimelineEvent step) => (LiveMessages.Timeline(step), LiveMessages.TimelineDetail(step));
}
