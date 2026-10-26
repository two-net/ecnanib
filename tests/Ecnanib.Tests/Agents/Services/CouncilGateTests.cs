using Ecnanib.Agents.Services;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Council;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>
/// FR-AI-08 and NFR-COST-01 as the gate decides them: an open position, or an entry that is possible and wanted, needs
/// the council; everything else ends without a provider call and says why. The window and the cap are judged as the
/// hard limits judge them, so a skipped council is one they would have refused.
/// </summary>
public class CouncilGateTests
{
    private static readonly DateTimeOffset Close = CouncilTestHost.CandleClose;

    [Fact]
    public void ASetupWithEntriesAllowedNeedsTheCouncil()
    {
        var need = Decide(CouncilTestHost.NewAgent(), CouncilTestHost.Output(CouncilTestHost.LongSetup()));

        Assert.True(need.Needed);
        Assert.Equal("the strategy reports a setup", need.Detail);
    }

    [Fact]
    public void NoSetupNoPositionAndNoDiscretionIsNoActionWithTheEnginesNoteOrTheReason()
    {
        var agent = CouncilTestHost.NewAgent();

        var noted = Decide(agent, CouncilTestHost.Output(null, note: "Long bias; no setup"));
        var bare = Decide(agent, CouncilTestHost.Output(null, note: null));

        Assert.False(noted.Needed);
        Assert.Equal("Long bias; no setup", noted.Detail);
        Assert.False(bare.Needed);
        Assert.Equal("No setup and no open position.", bare.Detail);
    }

    /// <summary>An open position is managed by the council whatever the state, also one flagged unmanaged: its Close or Modify is the only way left to act on it.</summary>
    [Theory]
    [InlineData(AgentState.Running, false, true)]
    [InlineData(AgentState.Paused, false, false)]
    [InlineData(AgentState.Running, true, false)]
    [InlineData(AgentState.Paused, true, false)]
    public void AnOpenPositionAlwaysNeedsTheCouncil(AgentState state, bool unmanaged, bool entriesAllowed)
    {
        var agent = CouncilTestHost.NewAgent(state: state);
        var trade = CouncilTestHost.OpenTrade(agent);
        trade.IsUnmanaged = unmanaged;

        var need = Decide(agent, CouncilTestHost.Output(null, note: null), entriesAllowed, trades: [trade]);

        Assert.True(need.Needed);
        Assert.Equal("a position is open", need.Detail);
    }

    [Fact]
    public void APendingEntryAloneIsNotAPosition()
    {
        var agent = CouncilTestHost.NewAgent();

        var need = Decide(agent, CouncilTestHost.Output(null, note: null), orders: [CouncilTestHost.EntryOrder(agent)]);

        Assert.False(need.Needed);
        Assert.Equal("No setup and no open position.", need.Detail);
    }

    [Fact]
    public void DiscretionaryModeNeedsTheCouncilWhenAnEntryIsPossible()
    {
        var need = Decide(CouncilTestHost.NewAgent(discretionary: true), CouncilTestHost.Output(null, note: "Long bias; no setup"));

        Assert.True(need.Needed);
        Assert.Equal("discretionary mode is on", need.Detail);
    }

    /// <summary>The hard limits refuse an entry inside a no-entry window (FR-STR-05, FR-AI-11), so a discretionary council would be paid for nothing.</summary>
    [Fact]
    public void DiscretionaryModeInsideANoEntryWindowIsNoAction()
    {
        var agent = CouncilTestHost.NewAgent(discretionary: true, configure: a => a.EntryWindows.Add(new EntryWindow { Kind = EntryWindowKind.OneOff, StartAt = Close.AddMinutes(-30), EndAt = Close.AddMinutes(30) }));

        var need = Decide(agent, CouncilTestHost.Output(null, note: "Long bias; no setup"));

        Assert.False(need.Needed);
        Assert.Equal("No new entry: a no-entry window is active at the candle close. Long bias; no setup", need.Detail);
    }

    /// <summary>A window's end is exclusive: one that ends at the close is over, as it is for the hard limits.</summary>
    [Fact]
    public void AWindowThatEndedAtTheCloseDoesNotHoldTheCouncilBack()
    {
        var agent = CouncilTestHost.NewAgent(discretionary: true, configure: a => a.EntryWindows.Add(new EntryWindow { Kind = EntryWindowKind.OneOff, StartAt = Close.AddHours(-2), EndAt = Close }));

        var need = Decide(agent, CouncilTestHost.Output(null, note: null));

        Assert.True(need.Needed);
    }

    [Fact]
    public void APausedDiscretionaryAgentWithoutAPositionIsNoAction()
    {
        var agent = CouncilTestHost.NewAgent(state: AgentState.Paused, discretionary: true);

        var need = Decide(agent, CouncilTestHost.Output(null, note: null), entriesAllowed: false);

        Assert.False(need.Needed);
        Assert.Equal("Paused: no open position to manage.", need.Detail);
    }

    /// <summary>Entries blocked by the management step (NFR-REL-03) or taken by a fallback entry this candle open nothing, so only a position would make the council worth asking.</summary>
    [Theory]
    [InlineData("Position management failed; see the log.", "No new entry this candle.")]
    [InlineData(null, "No setup; the open entry is managed.")]
    public void BlockedEntriesAreNoActionWithoutAPosition(string? entriesBlocked, string expected)
    {
        var agent = CouncilTestHost.NewAgent();

        var need = Decide(agent, CouncilTestHost.Output(CouncilTestHost.LongSetup(), note: null), entriesAllowed: false, entriesBlocked: entriesBlocked, orders: [CouncilTestHost.EntryOrder(agent)]);

        Assert.False(need.Needed);
        Assert.Equal(expected, need.Detail);
    }

    /// <summary>The hard limits count positions and pending entries against the smaller of the agent's and the system cap (FR-AGT-14); a full cap leaves nothing to open.</summary>
    [Fact]
    public void AFullPositionCapIsNoAction()
    {
        var agent = CouncilTestHost.NewAgent();

        var need = Decide(agent, CouncilTestHost.Output(CouncilTestHost.LongSetup(), note: "pullback setup"), orders: [CouncilTestHost.EntryOrder(agent)]);

        Assert.False(need.Needed);
        Assert.Equal("No new entry: 1 of 1 already open (positions and pending entries). pullback setup", need.Detail);
    }

    [Fact]
    public void TheSystemCapBoundsTheAgentsOwn()
    {
        var agent = CouncilTestHost.NewAgent(configure: a => a.RiskLimits.MaxOpenPositions = 5);

        var need = Decide(agent, CouncilTestHost.Output(CouncilTestHost.LongSetup(), note: null), orders: [CouncilTestHost.EntryOrder(agent)], systemCap: 1);

        Assert.False(need.Needed);
        Assert.Equal("No new entry: 1 of 1 already open (positions and pending entries).", need.Detail);
    }

    [Fact]
    public void RoomUnderTheCapNeedsTheCouncil()
    {
        var agent = CouncilTestHost.NewAgent(configure: a => a.RiskLimits.MaxOpenPositions = 2);

        var need = Decide(agent, CouncilTestHost.Output(CouncilTestHost.LongSetup()), orders: [CouncilTestHost.EntryOrder(agent)]);

        Assert.True(need.Needed);
    }

    private static CouncilNeed Decide(Agent agent, Ecnanib.Agents.Abstractions.StrategyOutput output, bool entriesAllowed = true, string? entriesBlocked = null,
        IReadOnlyList<Trade>? trades = null, IReadOnlyList<Order>? orders = null, int systemCap = 5) =>
        CouncilGate.Decide(agent, output, entriesAllowed, entriesBlocked, trades ?? [], orders ?? [], systemCap, Close);
}
