using Ecnanib.Agents.Configuration;
using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Council.Services;

/// <summary>The hard limits applied after the Risk Manager (FR-AI-11, FR-AI-15; FR-EXE-04 and FR-EXE-09 without a venue).</summary>
public class HardLimitsTests
{
    private static readonly RiskCapsOptions Caps = new();

    [Fact]
    public void AValidEntryPassesEveryLimit()
    {
        var outcome = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m, [108m], 1m), CouncilTestHost.Context(), Caps);

        Assert.False(outcome.Rejected);
        Assert.Equal(DecisionAction.OpenLong, outcome.Action);
        Assert.Equal(100m, outcome.EntryPrice);
        Assert.Equal(96m, outcome.StopPrice);
        Assert.Equal([108m], outcome.Targets);
        Assert.Equal(1m, outcome.RiskPercent);
        Assert.Equal(
            [LimitNames.EntriesAllowed, LimitNames.NoEntryWindow, LimitNames.SetupRequired, LimitNames.Direction, LimitNames.DailyLoss, LimitNames.OpenPositions, LimitNames.StopRequired, LimitNames.StopSide, LimitNames.StopDistance, LimitNames.RiskPerTrade],
            outcome.Checks.Select(c => c.Limit));
        Assert.All(outcome.Checks, c => Assert.Equal(LimitCheckStatus.Pass, c.Status));
    }

    [Fact]
    public void EntriesNotAllowedRejectsAnEntry()
    {
        var outcome = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m), CouncilTestHost.Context(entriesAllowed: false), Caps);

        AssertRejected(outcome, LimitNames.EntriesAllowed);
    }

    [Fact]
    public void AnActiveNoEntryWindowRejectsAnEntry()
    {
        var agent = CouncilTestHost.NewAgent(configure: a => a.EntryWindows.Add(new EntryWindow { Kind = EntryWindowKind.Recurring, StartDay = DayOfWeek.Monday, StartTime = new TimeOnly(0, 0), EndDay = DayOfWeek.Monday, EndTime = new TimeOnly(23, 0) }));

        var outcome = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m), CouncilTestHost.Context(agent), Caps);

        AssertRejected(outcome, LimitNames.NoEntryWindow);
    }

    [Fact]
    public void WithoutASetupOnlyDiscretionaryModeMayEnter()
    {
        var document = Open(DecisionAction.OpenLong, 100m, 96m);

        AssertRejected(HardLimits.Apply(document, CouncilTestHost.Context(output: CouncilTestHost.Output(null)), Caps), LimitNames.SetupRequired);
        Assert.False(HardLimits.Apply(document, CouncilTestHost.Context(CouncilTestHost.NewAgent(discretionary: true), CouncilTestHost.Output(null)), Caps).Rejected);
    }

    [Fact]
    public void AnEntryAgainstTheSetupSideNeedsDiscretionaryMode()
    {
        var document = Open(DecisionAction.OpenShort, 100m, 104m, [92m]);

        AssertRejected(HardLimits.Apply(document, CouncilTestHost.Context(CouncilTestHost.NewAgent(MarketType.UsdFutures)), Caps), LimitNames.SetupRequired);
        Assert.False(HardLimits.Apply(document, CouncilTestHost.Context(CouncilTestHost.NewAgent(MarketType.UsdFutures, discretionary: true)), Caps).Rejected);
    }

    [Fact]
    public void ASpotAgentNeverOpensAShort()
    {
        var outcome = HardLimits.Apply(Open(DecisionAction.OpenShort, 100m, 104m), CouncilTestHost.Context(CouncilTestHost.NewAgent(discretionary: true)), Caps);

        AssertRejected(outcome, LimitNames.Direction);
    }

    [Fact]
    public void TheDailyLossPauseRejectsAnEntry()
    {
        var agent = CouncilTestHost.NewAgent(state: AgentState.Paused, configure: a => a.PauseCause = PauseCause.DailyLoss);

        var outcome = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m), CouncilTestHost.Context(agent, entriesAllowed: true), Caps);

        AssertRejected(outcome, LimitNames.DailyLoss);
    }

    [Fact]
    public void ThePositionCapCountsPositionsAndPendingEntries()
    {
        var agent = CouncilTestHost.NewAgent();
        var document = Open(DecisionAction.OpenLong, 100m, 96m);

        AssertRejected(HardLimits.Apply(document, CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent)]), Caps), LimitNames.OpenPositions);
        AssertRejected(HardLimits.Apply(document, CouncilTestHost.Context(agent, orders: [CouncilTestHost.EntryOrder(agent)]), Caps), LimitNames.OpenPositions);

        agent.RiskLimits.MaxOpenPositions = 2;
        Assert.False(HardLimits.Apply(document, CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent)]), Caps).Rejected);

        agent.RiskLimits.MaxOpenPositions = 10;
        AssertRejected(HardLimits.Apply(document, CouncilTestHost.Context(agent, trades: Enumerable.Range(0, 5).Select(_ => CouncilTestHost.OpenTrade(agent)).ToList()), Caps), LimitNames.OpenPositions);
    }

    [Fact]
    public void AnEntryWithoutAStopOrWithTheStopOnTheWrongSideIsRejected()
    {
        AssertRejected(HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, null), CouncilTestHost.Context(), Caps), LimitNames.StopRequired);
        AssertRejected(HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 101m), CouncilTestHost.Context(), Caps), LimitNames.StopSide);
        AssertRejected(HardLimits.Apply(Open(DecisionAction.OpenShort, 100m, 99m), CouncilTestHost.Context(CouncilTestHost.NewAgent(MarketType.UsdFutures, discretionary: true)), Caps), LimitNames.StopSide);
    }

    [Fact]
    public void TheStopDistanceIsBoundByTheStrategy()
    {
        var context = CouncilTestHost.Context();

        AssertRejected(HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 93m), context, Caps), LimitNames.StopDistance);
        var within = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 94m), context, Caps);
        Assert.False(within.Rejected);
        Assert.Equal("3 ATR, at most 3", within.Checks.Single(c => c.Limit == LimitNames.StopDistance).Detail);

        var noAtr = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 50m), CouncilTestHost.Context(output: CouncilTestHost.Output(CouncilTestHost.LongSetup(), atr: 0m)), Caps);
        Assert.False(noAtr.Rejected);
        Assert.StartsWith("not applicable", noAtr.Checks.Single(c => c.Limit == LimitNames.StopDistance).Detail);
    }

    [Fact]
    public void AMarketEntryIsMeasuredAgainstTheLastClose()
    {
        var document = new FinalDecisionDocument("ok", 0.8m, DecisionAction.OpenLong, OrderType.Market, null, 96m, [108m], [], 1m, "go", null);

        var outcome = HardLimits.Apply(document, CouncilTestHost.Context(), Caps);

        Assert.False(outcome.Rejected);
        Assert.Equal(OrderType.Market, outcome.EntryType);
        Assert.Null(outcome.EntryPrice);
        Assert.Contains("entry 100", outcome.Checks.Single(c => c.Limit == LimitNames.StopSide).Detail);
    }

    [Fact]
    public void RiskPerTradeIsClampedToTheAgentLimitAndTheCap()
    {
        var context = CouncilTestHost.Context();

        var clamped = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m, risk: 5m), context, Caps);
        Assert.False(clamped.Rejected);
        Assert.Equal(1m, clamped.RiskPercent);
        Assert.Equal(LimitCheckStatus.Clamped, clamped.Checks.Single(c => c.Limit == LimitNames.RiskPerTrade).Status);

        var defaulted = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m, risk: null), context, Caps);
        Assert.Equal(1m, defaulted.RiskPercent);
        Assert.Contains("not given", defaulted.Checks.Single(c => c.Limit == LimitNames.RiskPerTrade).Detail);

        var agent = CouncilTestHost.NewAgent(configure: a => a.RiskLimits.RiskPerTradePercent = 5m);
        var capped = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m, risk: 3m), CouncilTestHost.Context(agent), new RiskCapsOptions { MaxRiskPerTradePercent = 2m });
        Assert.Equal(2m, capped.RiskPercent);
    }

    [Fact]
    public void LeverageAboveTheCapRejectsAFuturesEntry()
    {
        var agent = CouncilTestHost.NewAgent(MarketType.UsdFutures, configure: a => a.Leverage = 25);

        var outcome = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m), CouncilTestHost.Context(agent), Caps);

        AssertRejected(outcome, LimitNames.Leverage);
        Assert.DoesNotContain(HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m), CouncilTestHost.Context(), Caps).Checks, c => c.Limit == LimitNames.Leverage);
    }

    [Fact]
    public void TargetsOnTheWrongSideAreDropped()
    {
        var outcome = HardLimits.Apply(Open(DecisionAction.OpenLong, 100m, 96m, [108m, 90m, 115m]), CouncilTestHost.Context(), Caps);

        Assert.False(outcome.Rejected);
        Assert.Equal([108m, 115m], outcome.Targets);
        Assert.Equal(LimitCheckStatus.Clamped, outcome.Checks.Single(c => c.Limit == LimitNames.Targets).Status);
    }

    [Fact]
    public void AManagingActionWithoutAPositionIsNoTrade()
    {
        foreach (var action in new[] { DecisionAction.Hold, DecisionAction.Modify, DecisionAction.Close })
        {
            var outcome = HardLimits.Apply(Manage(action, stop: 97m), CouncilTestHost.Context(), Caps);

            Assert.Equal(DecisionAction.NoTrade, outcome.Action);
            Assert.Equal(LimitCheckStatus.Fail, Assert.Single(outcome.Checks).Status);
            Assert.Equal(LimitNames.PositionRequired, outcome.Checks[0].Limit);
        }
    }

    [Fact]
    public void HoldKeepsTheStopAndTargets()
    {
        var agent = CouncilTestHost.NewAgent();
        var context = CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent)]);

        var plain = HardLimits.Apply(Manage(DecisionAction.Hold), context, Caps);
        Assert.Equal(DecisionAction.Hold, plain.Action);
        Assert.Single(plain.Checks);

        var withStop = HardLimits.Apply(Manage(DecisionAction.Hold, stop: 97m, targets: [120m]), context, Caps);
        Assert.Equal(DecisionAction.Hold, withStop.Action);
        Assert.Null(withStop.StopPrice);
        Assert.Empty(withStop.Targets);
        Assert.Equal(LimitCheckStatus.Clamped, withStop.Checks.Single(c => c.Limit == LimitNames.StopTightenOnly).Status);
    }

    [Fact]
    public void ModifyOnlyTightensTheStop()
    {
        var agent = CouncilTestHost.NewAgent();
        var context = CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent, stop: 95m)]);

        var tightened = HardLimits.Apply(Manage(DecisionAction.Modify, stop: 97m), context, Caps);
        Assert.Equal(DecisionAction.Modify, tightened.Action);
        Assert.Equal(97m, tightened.StopPrice);
        Assert.Equal("stop tightened from 95 to 97", tightened.Checks.Single(c => c.Limit == LimitNames.StopTightenOnly).Detail);

        var widened = HardLimits.Apply(Manage(DecisionAction.Modify, stop: 93m), context, Caps);
        Assert.Equal(DecisionAction.Hold, widened.Action);
        Assert.Null(widened.StopPrice);
        Assert.Equal(2, widened.Checks.Count(c => c.Limit == LimitNames.StopTightenOnly && c.Status == LimitCheckStatus.Clamped));

        var unchanged = HardLimits.Apply(Manage(DecisionAction.Modify, stop: 95m, targets: [120m]), context, Caps);
        Assert.Equal(DecisionAction.Modify, unchanged.Action);
        Assert.Null(unchanged.StopPrice);
        Assert.Equal([120m], unchanged.Targets);
    }

    [Fact]
    public void AShortPositionMirrorsTheStopRules()
    {
        var agent = CouncilTestHost.NewAgent(MarketType.UsdFutures);
        var context = CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent, PositionSide.Short, stop: 105m)]);

        Assert.Equal(103m, HardLimits.Apply(Manage(DecisionAction.Modify, stop: 103m), context, Caps).StopPrice);
        Assert.Equal(DecisionAction.Hold, HardLimits.Apply(Manage(DecisionAction.Modify, stop: 107m), context, Caps).Action);
        Assert.Equal(DecisionAction.Hold, HardLimits.Apply(Manage(DecisionAction.Modify, stop: 100m), context, Caps).Action);
        Assert.Equal([90m], HardLimits.Apply(Manage(DecisionAction.Modify, targets: [90m, 110m]), context, Caps).Targets);
    }

    [Fact]
    public void AStopThatWouldTriggerAtOnceFailsTheModify()
    {
        var agent = CouncilTestHost.NewAgent();
        var context = CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent, stop: 95m)]);

        var outcome = HardLimits.Apply(Manage(DecisionAction.Modify, stop: 100m, partial: 0.5m), context, Caps);

        Assert.Equal(DecisionAction.Hold, outcome.Action);
        Assert.Null(outcome.PartialFraction);
        Assert.Equal(LimitCheckStatus.Fail, outcome.Checks.Single(c => c.Limit == LimitNames.StopSide).Status);
    }

    [Fact]
    public void AnUnmanagedPositionWithoutAStopAcceptsAnyStopOnTheRightSide()
    {
        var agent = CouncilTestHost.NewAgent();
        var context = CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent, stop: null)]);

        var outcome = HardLimits.Apply(Manage(DecisionAction.Modify, stop: 80m), context, Caps);

        Assert.Equal(DecisionAction.Modify, outcome.Action);
        Assert.Equal(80m, outcome.StopPrice);
        Assert.Equal("stop set to 80", outcome.Checks.Single(c => c.Limit == LimitNames.StopTightenOnly).Detail);
    }

    [Fact]
    public void APartialTakeProfitAndCloseNeedNoPrices()
    {
        var agent = CouncilTestHost.NewAgent();
        var context = CouncilTestHost.Context(agent, trades: [CouncilTestHost.OpenTrade(agent)]);

        var partial = HardLimits.Apply(Manage(DecisionAction.Modify, partial: 0.5m), context, Caps);
        Assert.Equal(DecisionAction.Modify, partial.Action);
        Assert.Equal(0.5m, partial.PartialFraction);
        Assert.Equal("take 50% of the position", partial.Checks.Single(c => c.Limit == LimitNames.PartialFraction).Detail);

        var close = HardLimits.Apply(Manage(DecisionAction.Close, stop: 1m), context, Caps);
        Assert.Equal(DecisionAction.Close, close.Action);
        Assert.Single(close.Checks);
    }

    [Fact]
    public void NoTradePassesThroughWithoutChecks()
    {
        var outcome = HardLimits.Apply(new FinalDecisionDocument("nothing", 0.9m, DecisionAction.NoTrade, null, null, null, [], [], null, "flat", null), CouncilTestHost.Context(), Caps);

        Assert.Equal(DecisionAction.NoTrade, outcome.Action);
        Assert.Empty(outcome.Checks);
        Assert.Equal("[]", HardLimits.ToJson(outcome.Checks).GetRawText());
    }

    [Fact]
    public void TheChecksSerialiseAsTheAppendixCLimitsCheck()
    {
        var json = HardLimits.ToJson([new LimitCheck(LimitNames.RiskPerTrade, LimitCheckStatus.Clamped, "5% reduced to 1%")]).GetRawText();

        Assert.Equal("""[{"limit":"riskPerTrade","status":"clamped","detail":"5% reduced to 1%"}]""", json);
    }

    private static FinalDecisionDocument Open(DecisionAction action, decimal? entry, decimal? stop, decimal[]? targets = null, decimal? risk = 1m) =>
        new("ok", 0.8m, action, OrderType.Limit, entry, stop, targets ?? [108m], [], risk, "go", null);

    private static FinalDecisionDocument Manage(DecisionAction action, decimal? stop = null, decimal[]? targets = null, decimal? partial = null) =>
        new("ok", 0.8m, action, null, null, stop, targets ?? [], [], null, "manage", partial);

    private static void AssertRejected(LimitsOutcome outcome, string limit)
    {
        Assert.True(outcome.Rejected);
        Assert.Equal(DecisionAction.NoTrade, outcome.Action);
        Assert.Null(outcome.EntryPrice);
        Assert.Null(outcome.StopPrice);
        Assert.Null(outcome.RiskPercent);
        Assert.Empty(outcome.Targets);
        Assert.Contains(limit, outcome.FailedLimits);
    }
}
