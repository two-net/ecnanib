using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Experience.Services;
using Ecnanib.Tests.Council;

namespace Ecnanib.Tests.Experience.Services;

/// <summary>When a decision leaves a shadow outcome and why (FR-EXP-03), and the entry timeout a shadow follows.</summary>
public class ExperienceTrackerTests
{
    [Fact]
    public void AVetoALimitsRejectionAndANoDecisionLeaveAShadowWithTheirReason()
    {
        var setup = CouncilTestHost.Output(CouncilTestHost.LongSetup());

        Assert.Equal(ShadowDetail.Veto, ExperienceTracker.ReasonFor(Decision(DecisionAction.NoTrade, RoleOutputStatus.Succeeded, DecisionAction.NoTrade), setup, true));
        Assert.Equal(ShadowDetail.Limits, ExperienceTracker.ReasonFor(Decision(DecisionAction.NoTrade, RoleOutputStatus.Succeeded, DecisionAction.OpenLong), setup, true));
        Assert.Equal(ShadowDetail.NoDecision, ExperienceTracker.ReasonFor(Decision(DecisionAction.NoTrade, RoleOutputStatus.Failed, null), setup, true));
        Assert.Equal(ShadowDetail.NoDecision, ExperienceTracker.ReasonFor(Decision(DecisionAction.NoTrade, RoleOutputStatus.TimedOut, null), setup, true));
        Assert.Equal(ShadowDetail.NoDecision, ExperienceTracker.ReasonFor(new Decision { Rationale = "", FinalAction = DecisionAction.NoTrade }, setup, true));
    }

    [Fact]
    public void AnOpenedEntryAManagingDecisionNoSetupOrNoEntriesAllowedLeaveNone()
    {
        var setup = CouncilTestHost.Output(CouncilTestHost.LongSetup());
        var none = CouncilTestHost.Output(null);

        Assert.Null(ExperienceTracker.ReasonFor(Decision(DecisionAction.OpenLong, RoleOutputStatus.Succeeded, DecisionAction.OpenLong), setup, true));
        Assert.Null(ExperienceTracker.ReasonFor(Decision(DecisionAction.Hold, RoleOutputStatus.Succeeded, DecisionAction.Hold), setup, true));
        Assert.Null(ExperienceTracker.ReasonFor(Decision(DecisionAction.NoTrade, RoleOutputStatus.Succeeded, DecisionAction.NoTrade), none, true));
        Assert.Null(ExperienceTracker.ReasonFor(Decision(DecisionAction.NoTrade, RoleOutputStatus.Succeeded, DecisionAction.NoTrade), setup, false));
    }

    [Fact]
    public void TheEntryTimeoutIsTheStrategysCommonParameterOrOneCandle()
    {
        Assert.Equal(3, ExperienceTracker.EntryTimeout(new Dictionary<string, JsonElement> { [StrategyParameters.EntryTimeoutCandles] = JsonSerializer.SerializeToElement(3) }));
        Assert.Equal(1, ExperienceTracker.EntryTimeout(new Dictionary<string, JsonElement>()));
        Assert.Equal(1, ExperienceTracker.EntryTimeout(new Dictionary<string, JsonElement> { [StrategyParameters.EntryTimeoutCandles] = JsonSerializer.SerializeToElement("x") }));
        Assert.Equal(1, ExperienceTracker.EntryTimeout(new Dictionary<string, JsonElement> { [StrategyParameters.EntryTimeoutCandles] = JsonSerializer.SerializeToElement(0) }));
    }

    private static Decision Decision(DecisionAction final, RoleOutputStatus riskManager, DecisionAction? proposed) => new()
    {
        Rationale = "",
        FinalAction = final,
        RoleOutputs =
        [
            new DecisionRoleOutput { Role = CouncilRole.MarketAnalyst, Sequence = 1, Provider = AiProvider.Gpt, Model = "g", Status = RoleOutputStatus.Succeeded, ProposedAction = DecisionAction.OpenLong },
            new DecisionRoleOutput { Role = CouncilRole.RiskManager, Sequence = 4, Provider = AiProvider.Claude, Model = "c", Status = riskManager, ProposedAction = proposed },
        ],
    };
}
