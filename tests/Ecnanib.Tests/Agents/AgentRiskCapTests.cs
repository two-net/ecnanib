using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Agents;

/// <summary>FR-RISK-11: no agent setting may exceed the global hard caps in configuration, checked on save and at activation.</summary>
public sealed class AgentRiskCapTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task Settings_beyond_the_caps_are_refused_on_save_and_named()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var settings = AgentApp.Ready(user) with
        {
            Risk = RiskSettings.Default with { MaxLeverage = 6, MaxRiskPerTradePercent = 2.5m, MinRewardToRisk = 0.9m, LiquidationBufferMultiple = 1.5m },
        };

        var result = await app.Agents.CreateAsync(user.Id, settings, Cancellation);

        Assert.Equal(AgentSaveOutcome.Invalid, result.Outcome);
        Assert.Contains(AgentField.Risk, result.Fields!);
        Assert.Equal(
            [RiskSetting.MaxRiskPerTradePercent, RiskSetting.MaxLeverage, RiskSetting.MinRewardToRisk, RiskSetting.LiquidationBufferMultiple],
            result.RiskSettings);
    }

    [Fact]
    public async Task A_setting_exactly_at_its_cap_is_saved_and_activated()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var settings = AgentApp.Ready(user) with
        {
            Risk = RiskSettings.Default with { MaxLeverage = 5, MaxRiskPerTradePercent = 2m, MinRewardToRisk = 1m, LiquidationBufferMultiple = 2m },
        };

        var agent = await app.CreateAsync(user.Id, settings);

        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Outcome);
    }

    [Fact]
    public async Task The_caps_come_from_configuration()
    {
        await using var app = new AgentApp(postgres, new() { ["RiskCaps:MaxLeverage"] = "1" });
        var user = await app.UserAsync();

        var result = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user), Cancellation);

        // The SRS's default leverage of 2 is above a cap of 1.
        Assert.Equal(AgentSaveOutcome.Invalid, result.Outcome);
        Assert.Equal([RiskSetting.MaxLeverage], result.RiskSettings);
    }

    [Fact]
    public async Task An_agent_saved_before_the_caps_were_tightened_cannot_be_activated_or_edited_while_active()
    {
        await using var app = new AgentApp(postgres, new() { ["RiskCaps:MaxLeverage"] = "3" });
        var user = await app.UserAsync();
        var draft = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Draft"));
        var active = await app.ActiveAsync(user, "Active", Market.Futures);

        // As if each had been saved when the cap was higher.
        await Sql.ExecuteAsync(app.ConnectionString, "UPDATE agents SET risk_max_leverage = 4", Cancellation);

        var refused = await app.Agents.ActivateAsync(user.Id, draft.Id, Cancellation);
        Assert.Equal(ActivationOutcome.Refused, refused.Outcome);
        Assert.Contains(refused.Problems!, problem => problem.Kind == ActivationProblemKind.RiskSettingsAboveCaps);
        Assert.DoesNotContain(refused.Problems!, problem => problem.Kind == ActivationProblemKind.RiskSettingsInvalid);

        // Editing anything else on the Active one runs the activation checks, which refuse it too.
        var stored = (await app.Agents.FindAsync(user.Id, active.Id, Cancellation))!;
        var edit = await app.Agents.UpdateAsync(user.Id, active.Id, stored.Version, stored.Settings with { Description = "Edited" }, Cancellation);
        Assert.Equal(AgentSaveOutcome.Invalid, edit.Outcome);
        Assert.Equal([RiskSetting.MaxLeverage], edit.RiskSettings);
    }
}
