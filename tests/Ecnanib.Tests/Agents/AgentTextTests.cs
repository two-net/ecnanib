using System.Reflection;
using Ecnanib.Components.Account;
using Ecnanib.Components.Agents;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Tests.Agents;

/// <summary>EXT-UI-02, FR-AUD-03: everything about an agent that a page shows has text of its own.</summary>
public sealed class AgentTextTests
{
    /// <summary>A field without text is left off the activity page, without a word.</summary>
    [Fact]
    public void Every_field_in_an_agents_summary_has_text()
    {
        var fields = typeof(AgentAuditFields)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.Name.EndsWith("Prefix", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Concat(Enum.GetValues<AiRole>().Select(AgentAuditFields.RoleField))
            .Concat(Enum.GetValues<AiRole>().Select(AgentAuditFields.FallbackField))
            .Concat(Enum.GetValues<RiskSetting>().Select(AgentAuditFields.RiskField))
            .ToList();

        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(ActivityText.FieldOf(field)), field));
    }

    [Fact]
    public void Every_value_has_text_of_its_own()
    {
        Distinct(Enum.GetValues<AgentStatus>().Select(AgentText.StatusOf));
        Distinct(Enum.GetValues<AgentStatusReason>().Select(AgentText.ReasonOf));
        Distinct(Enum.GetValues<AgentStatusReason>().Select(AgentText.ExplanationOf).OfType<string>());
        Distinct(Enum.GetValues<Market>().Select(AgentText.MarketOf));
        Distinct(Enum.GetValues<Timeframe>().Select(AgentText.TimeframeOf));
        Distinct(Enum.GetValues<ExecutionMode>().Select(AgentText.ExecutionModeOf));
        Distinct(Enum.GetValues<AiRole>().Select(AgentText.RoleOf));
        Distinct(Enum.GetValues<RiskSetting>().Select(AgentText.RiskSettingOf));
        Distinct(Enum.GetValues<RiskSetting>().Select(AgentText.RiskHintOf));
        Distinct(Enum.GetValues<AgentField>().Select(AgentText.FieldErrorOf));
        Distinct(Enum.GetValues<TransitionOutcome>().Select(AgentText.TransitionErrorOf).OfType<string>());
        Distinct(Enum.GetValues<ActivationWarningKind>().Select(kind => AgentText.WarningOf(new ActivationWarning(kind, AiRole.Strategist))));

        // A refusal of the pair reads the same whether it came from saving or activating.
        Assert.All(Enum.GetValues<AgentSaveOutcome>().Where(outcome => outcome != AgentSaveOutcome.Saved), outcome =>
            Assert.False(string.IsNullOrWhiteSpace(AgentText.SaveErrorOf(outcome))));
        Assert.All(Enum.GetValues<ActivationProblemKind>(), kind =>
            Assert.False(string.IsNullOrWhiteSpace(AgentText.ProblemOf(new ActivationProblem(kind, AiRole.Strategist, "Other")))));
    }

    [Theory]
    [InlineData("Status", "Paused", "Active", "from Paused to Active")]
    [InlineData("Reason", null, "CredentialDeleted", "Binance key deleted")]
    [InlineData("Market", null, "Futures", "USDⓈ-M Futures")]
    [InlineData("Timeframe", "OneHour", "FourHours", "from 1H to 4H")]
    [InlineData("ExecutionMode", "Confirm", "Autonomous", "from Confirm each trade to Autonomous")]
    [InlineData("ExperienceMemory", "True", "False", "from On to Off")]
    [InlineData("Role.Strategist", null, "OpenAI gpt-6", "OpenAI gpt-6")]
    public void An_agents_change_reads_as_a_line_of_text(string field, string? before, string after, string text)
    {
        Assert.Equal(text, ActivityText.Describe(new ActivityChange(field, before, after)));
    }

    private static void Distinct(IEnumerable<string> texts)
    {
        var list = texts.ToList();

        Assert.All(list, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
    }
}
