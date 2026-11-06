using System.Reflection;
using Ecnanib.Components.Account;
using Ecnanib.Components.Approvals;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>EXT-UI-02, FR-APR-03, FR-AUD-03: everything about an approval that a page shows has text of its own, including why the Risk Engine refused one.</summary>
public sealed class ApprovalTextTests
{
    [Fact]
    public void Every_value_has_text_of_its_own()
    {
        Distinct(Enum.GetValues<ApprovalStatus>().Select(ApprovalText.StatusOf));
        Distinct(Enum.GetValues<ApprovalEnd>().Select(ApprovalText.EndOf));
        Distinct(Enum.GetValues<IntentAction>().Select(ApprovalText.ActionOf));
        Distinct(Enum.GetValues<VerdictKind>().Select(ApprovalText.VerdictOf));
        Distinct(Enum.GetValues<RiskReason>().Select(ApprovalText.ReasonOf));
        Distinct(Enum.GetValues<ExecutionStatus>().Select(ApprovalText.ExecutionOf));
        Distinct(Enum.GetValues<ExecutionReason>().Select(ApprovalText.ExecutionReasonOf));
    }

    /// <summary>A field without text is left off the activity page, without a word.</summary>
    [Fact]
    public void Every_field_in_an_approvals_summary_has_text()
    {
        var fields = typeof(ApprovalAuditFields)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);

        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(ActivityText.FieldOf(field)), field));
    }

    [Theory]
    [InlineData("TradeAction", null, "OpenLong", "Open long")]
    [InlineData("ApprovalStatus", "Pending", "Approving", "from Waiting for you to Being sent")]
    [InlineData("ApprovalStatus", "Approving", "Invalid", "from Being sent to Rejected by the risk rules")]
    [InlineData("Symbol", null, "BTCUSDT", "BTCUSDT")]
    public void An_approvals_summary_reads_as_words(string field, string? before, string after, string expected) =>
        Assert.Equal(expected, ActivityText.Describe(new(field, before, after)));

    [Fact]
    public void Every_status_says_how_it_went()
    {
        var proposal = new ApprovalProposal(
            Kernel.Market.Spot, Kernel.TradingEnvironment.Demo, "BTCUSDT", "BTC", "USDT", Kernel.Timeframe.OneHour, IntentAction.Buy,
            OrderSide.Buy, EntryType.Market, 0.01m, null, 60000m, 59900m, 60200m, 600m, 1m, 2m, null, "Why.", VerdictKind.Approve, "Fine.", []);

        Distinct(Enum.GetValues<ApprovalStatus>().Select(status => ApprovalText.OutcomeOf(new ApprovalRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), status, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, true, proposal, null, null))));
    }

    private static void Distinct(IEnumerable<string> texts)
    {
        var list = texts.ToList();

        Assert.All(list, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
    }
}
