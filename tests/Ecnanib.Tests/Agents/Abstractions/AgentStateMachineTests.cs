using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.Tests.Agents.Abstractions;

/// <summary>Appendix E as a table (FR-AGT-04) and the edit rules of FR-AGT-07.</summary>
public class AgentStateMachineTests
{
    [Theory]
    [InlineData(AgentState.Draft, AgentAction.Start, true)]
    [InlineData(AgentState.Draft, AgentAction.Pause, false)]
    [InlineData(AgentState.Draft, AgentAction.Resume, false)]
    [InlineData(AgentState.Draft, AgentAction.Stop, false)]
    [InlineData(AgentState.Draft, AgentAction.Delete, true)]
    [InlineData(AgentState.Running, AgentAction.Start, false)]
    [InlineData(AgentState.Running, AgentAction.Pause, true)]
    [InlineData(AgentState.Running, AgentAction.Resume, false)]
    [InlineData(AgentState.Running, AgentAction.Stop, true)]
    [InlineData(AgentState.Running, AgentAction.Delete, false)]
    [InlineData(AgentState.Paused, AgentAction.Start, false)]
    [InlineData(AgentState.Paused, AgentAction.Pause, false)]
    [InlineData(AgentState.Paused, AgentAction.Resume, true)]
    [InlineData(AgentState.Paused, AgentAction.Stop, true)]
    [InlineData(AgentState.Paused, AgentAction.Delete, false)]
    [InlineData(AgentState.Stopped, AgentAction.Start, true)]
    [InlineData(AgentState.Stopped, AgentAction.Pause, false)]
    [InlineData(AgentState.Stopped, AgentAction.Resume, false)]
    [InlineData(AgentState.Stopped, AgentAction.Stop, false)]
    [InlineData(AgentState.Stopped, AgentAction.Delete, true)]
    [InlineData(AgentState.Error, AgentAction.Start, true)]
    [InlineData(AgentState.Error, AgentAction.Pause, false)]
    [InlineData(AgentState.Error, AgentAction.Resume, false)]
    [InlineData(AgentState.Error, AgentAction.Stop, true)]
    [InlineData(AgentState.Error, AgentAction.Delete, false)]
    public void FollowsAppendixE(AgentState state, AgentAction action, bool allowed)
    {
        Assert.Equal(allowed, AgentStateMachine.Allows(state, action));
    }

    [Theory]
    [InlineData(AgentState.Draft)]
    [InlineData(AgentState.Running)]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Stopped)]
    [InlineData(AgentState.Error)]
    public void CloneIsAllowedEverywhereAndOnlyRunningOrPausedAreEvaluated(AgentState state)
    {
        Assert.True(AgentStateMachine.Allows(state, AgentAction.Clone));
        Assert.Equal(state is AgentState.Running or AgentState.Paused, AgentStateMachine.IsEvaluated(state));
    }

    [Theory]
    [InlineData(AgentField.Symbol, AgentState.Draft, true)]
    [InlineData(AgentField.Symbol, AgentState.Stopped, true)]
    [InlineData(AgentField.Symbol, AgentState.Paused, false)]
    [InlineData(AgentField.Symbol, AgentState.Error, false)]
    [InlineData(AgentField.Symbol, AgentState.Running, false)]
    [InlineData(AgentField.Timeframe, AgentState.Paused, false)]
    [InlineData(AgentField.MarketType, AgentState.Paused, false)]
    [InlineData(AgentField.Mode, AgentState.Paused, false)]
    [InlineData(AgentField.Strategy, AgentState.Paused, false)]
    [InlineData(AgentField.Parameters, AgentState.Paused, true)]
    [InlineData(AgentField.Council, AgentState.Paused, true)]
    [InlineData(AgentField.RiskLimits, AgentState.Paused, true)]
    [InlineData(AgentField.Parameters, AgentState.Error, true)]
    [InlineData(AgentField.Parameters, AgentState.Running, false)]
    [InlineData(AgentField.Council, AgentState.Running, false)]
    [InlineData(AgentField.RiskLimits, AgentState.Running, false)]
    [InlineData(AgentField.ExchangeKey, AgentState.Error, true)]
    [InlineData(AgentField.ExchangeKey, AgentState.Paused, false)]
    [InlineData(AgentField.Leverage, AgentState.Error, false)]
    [InlineData(AgentField.Name, AgentState.Running, true)]
    [InlineData(AgentField.Discretionary, AgentState.Running, false)]
    [InlineData(AgentField.Discretionary, AgentState.Paused, true)]
    public void EditsFollowTheStateRules(AgentField field, AgentState state, bool editable)
    {
        Assert.Equal(editable, AgentStateMachine.CanEdit(state, field));
    }
}
