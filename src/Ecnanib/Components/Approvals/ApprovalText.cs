using System.Globalization;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Components.Approvals;

/// <summary>
/// What an approval's statuses, actions, verdicts and outcomes are called, and why the Risk Engine refused one. Public so
/// that a test can prove each value has text: an issue that adds one adds its text here, or the page fails. The trace
/// viewer (#42) can take the Risk Engine's and execution's text from here.
/// </summary>
public static class ApprovalText
{
    public static string StatusOf(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Pending => Strings.ApprovalStatusPending,
        ApprovalStatus.Approving => Strings.ApprovalStatusApproving,
        ApprovalStatus.Executed => Strings.ApprovalStatusExecuted,
        ApprovalStatus.Invalid => Strings.ApprovalStatusInvalid,
        ApprovalStatus.Failed => Strings.ApprovalStatusFailed,
        ApprovalStatus.Rejected => Strings.ApprovalStatusRejected,
        ApprovalStatus.Expired => Strings.ApprovalStatusExpired,
        ApprovalStatus.Interrupted => Strings.ApprovalStatusInterrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Give the status its text in Strings.resx."),
    };

    /// <summary>Why it expired or couldn't be sent, as a sentence.</summary>
    public static string EndOf(ApprovalEnd end) => end switch
    {
        ApprovalEnd.WindowEnded => Strings.ApprovalEndWindowEnded,
        ApprovalEnd.AgentNotActive => Strings.ApprovalEndAgentNotActive,
        ApprovalEnd.Superseded => Strings.ApprovalEndSuperseded,
        ApprovalEnd.AgentChanged => Strings.ApprovalEndAgentChanged,
        ApprovalEnd.MarketData => Strings.ApprovalEndMarketData,
        ApprovalEnd.Account => Strings.ApprovalEndAccount,
        ApprovalEnd.Execution => Strings.ApprovalEndExecution,
        ApprovalEnd.ShuttingDown => Strings.ApprovalEndShuttingDown,
        ApprovalEnd.Unexpected => Strings.ApprovalEndUnexpected,
        _ => throw new ArgumentOutOfRangeException(nameof(end), end, "Give the end its text in Strings.resx."),
    };

    /// <summary>FR-CYC-03's actions, in words rather than color or a sign (FR-ANI-03).</summary>
    public static string ActionOf(IntentAction action) => action switch
    {
        IntentAction.Buy => Strings.ApprovalActionBuy,
        IntentAction.Sell => Strings.ApprovalActionSell,
        IntentAction.OpenLong => Strings.ApprovalActionOpenLong,
        IntentAction.OpenShort => Strings.ApprovalActionOpenShort,
        IntentAction.Close => Strings.ApprovalActionClose,
        IntentAction.AdjustStop => Strings.ApprovalActionAdjustStop,
        IntentAction.AdjustTarget => Strings.ApprovalActionAdjustTarget,
        IntentAction.CancelPending => Strings.ApprovalActionCancelPending,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Give the action its text in Strings.resx."),
    };

    public static string VerdictOf(VerdictKind verdict) => verdict switch
    {
        VerdictKind.Approve => Strings.ApprovalVerdictApprove,
        VerdictKind.Modify => Strings.ApprovalVerdictModify,
        VerdictKind.Reject => Strings.ApprovalVerdictReject,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Give the verdict its text in Strings.resx."),
    };

    /// <summary>Why a Risk Engine rule failed, as a sentence (FR-APR-03).</summary>
    public static string ReasonOf(RiskReason reason) => reason switch
    {
        RiskReason.SettingsOutOfRange => Strings.RiskReasonSettingsOutOfRange,
        RiskReason.NoCapital => Strings.RiskReasonNoCapital,
        RiskReason.InputInvalid => Strings.RiskReasonInputInvalid,
        RiskReason.ActionNotForMarket => Strings.RiskReasonActionNotForMarket,
        RiskReason.EntryTypeMissing => Strings.RiskReasonEntryTypeMissing,
        RiskReason.OppositePosition => Strings.RiskReasonOppositePosition,
        RiskReason.NoPosition => Strings.RiskReasonNoPosition,
        RiskReason.NoPendingEntry => Strings.RiskReasonNoPendingEntry,
        RiskReason.SymbolNotTrading => Strings.RiskReasonSymbolNotTrading,
        RiskReason.DailyLossLimitReached => Strings.RiskReasonDailyLossLimitReached,
        RiskReason.WeeklyLossLimitReached => Strings.RiskReasonWeeklyLossLimitReached,
        RiskReason.ConsecutiveLossesReached => Strings.RiskReasonConsecutiveLossesReached,
        RiskReason.EntriesPerDayReached => Strings.RiskReasonEntriesPerDayReached,
        RiskReason.CoolingDown => Strings.RiskReasonCoolingDown,
        RiskReason.EntryPending => Strings.RiskReasonEntryPending,
        RiskReason.FundingAgainstPosition => Strings.RiskReasonFundingAgainstPosition,
        RiskReason.FilterMissing => Strings.RiskReasonFilterMissing,
        RiskReason.PriceMissing => Strings.RiskReasonPriceMissing,
        RiskReason.MarkPriceMissing => Strings.RiskReasonMarkPriceMissing,
        RiskReason.PriceOutOfRange => Strings.RiskReasonPriceOutOfRange,
        RiskReason.PriceOutsideBand => Strings.RiskReasonPriceOutsideBand,
        RiskReason.StopMissing => Strings.RiskReasonStopMissing,
        RiskReason.StopWrongSide => Strings.RiskReasonStopWrongSide,
        RiskReason.AtrUnavailable => Strings.RiskReasonAtrUnavailable,
        RiskReason.StopTooClose => Strings.RiskReasonStopTooClose,
        RiskReason.StopTooFar => Strings.RiskReasonStopTooFar,
        RiskReason.StopWidened => Strings.RiskReasonStopWidened,
        RiskReason.StopUnchanged => Strings.RiskReasonStopUnchanged,
        RiskReason.TargetWrongSide => Strings.RiskReasonTargetWrongSide,
        RiskReason.RewardToRiskTooLow => Strings.RiskReasonRewardToRiskTooLow,
        RiskReason.AveragingDown => Strings.RiskReasonAveragingDown,
        RiskReason.RiskPercentInvalid => Strings.RiskReasonRiskPercentInvalid,
        RiskReason.CloseFractionInvalid => Strings.RiskReasonCloseFractionInvalid,
        RiskReason.BudgetUsed => Strings.RiskReasonBudgetUsed,
        RiskReason.NoCapacity => Strings.RiskReasonNoCapacity,
        RiskReason.QuantityTooSmall => Strings.RiskReasonQuantityTooSmall,
        RiskReason.NotionalTooSmall => Strings.RiskReasonNotionalTooSmall,
        RiskReason.NotionalTooLarge => Strings.RiskReasonNotionalTooLarge,
        RiskReason.Dust => Strings.RiskReasonDust,
        RiskReason.LiquidationUnknown => Strings.RiskReasonLiquidationUnknown,
        RiskReason.LeverageAboveBracket => Strings.RiskReasonLeverageAboveBracket,
        RiskReason.LiquidationTooClose => Strings.RiskReasonLiquidationTooClose,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Give the reason its text in Strings.resx."),
    };

    /// <summary>What execution did, as a sentence.</summary>
    public static string ExecutionOf(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Placed => Strings.ExecutionStatusPlaced,
        ExecutionStatus.Protected => Strings.ExecutionStatusProtected,
        ExecutionStatus.Closed => Strings.ExecutionStatusClosed,
        ExecutionStatus.Unprotected => Strings.ExecutionStatusUnprotected,
        ExecutionStatus.Cancelled => Strings.ExecutionStatusCancelled,
        ExecutionStatus.NotSent => Strings.ExecutionStatusNotSent,
        ExecutionStatus.Rejected => Strings.ExecutionStatusRejected,
        ExecutionStatus.NotPlaced => Strings.ExecutionStatusNotPlaced,
        ExecutionStatus.Unsettled => Strings.ExecutionStatusUnsettled,
        ExecutionStatus.Exited => Strings.ExecutionStatusExited,
        ExecutionStatus.Adjusted => Strings.ExecutionStatusAdjusted,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Give the status its text in Strings.resx."),
    };

    /// <summary>Why execution sent nothing, as a sentence.</summary>
    public static string ExecutionReasonOf(ExecutionReason reason) => reason switch
    {
        ExecutionReason.PastDeadline => Strings.ExecutionReasonPastDeadline,
        ExecutionReason.PositionOpen => Strings.ExecutionReasonPositionOpen,
        ExecutionReason.FuturesSetup => Strings.ExecutionReasonFuturesSetup,
        ExecutionReason.KeyUnusable => Strings.ExecutionReasonKeyUnusable,
        ExecutionReason.ExchangeUnavailable => Strings.ExecutionReasonExchangeUnavailable,
        ExecutionReason.OrderTypeUnsupported => Strings.ExecutionReasonOrderTypeUnsupported,
        ExecutionReason.NothingToCancel => Strings.ExecutionReasonNothingToCancel,
        ExecutionReason.Unsupported => Strings.ExecutionReasonUnsupported,
        ExecutionReason.NoPosition => Strings.ExecutionReasonNoPosition,
        ExecutionReason.ForeignPosition => Strings.ExecutionReasonForeignPosition,
        ExecutionReason.AgentNotActive => Strings.ExecutionReasonAgentNotActive,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Give the reason its text in Strings.resx."),
    };

    /// <summary>How a decided approval went, as one or more sentences (FR-APR-03: the reason is shown).</summary>
    public static string OutcomeOf(ApprovalRecord approval)
    {
        ArgumentNullException.ThrowIfNull(approval);

        var end = approval.End is { } known ? EndOf(known) : null;
        var execution = approval.Execution is { } result ? ResultOf(result) : null;

        string?[] sentences = approval.Status switch
        {
            ApprovalStatus.Pending => [Strings.ApprovalOutcomePending],
            ApprovalStatus.Approving => [Strings.ApprovalOutcomeApproving],
            ApprovalStatus.Executed => [Strings.ApprovalOutcomeExecuted, execution],
            ApprovalStatus.Invalid => [Strings.ApprovalOutcomeInvalid],

            // Binance's own answer says more than "it didn't take it".
            ApprovalStatus.Failed => [Strings.ApprovalOutcomeFailed, execution ?? end],
            ApprovalStatus.Rejected => [Strings.ApprovalOutcomeRejected],
            ApprovalStatus.Expired => [Strings.ApprovalOutcomeExpired, end],
            ApprovalStatus.Interrupted => [Strings.ApprovalOutcomeInterrupted],
            _ => throw new ArgumentOutOfRangeException(nameof(approval), approval.Status, "Give the status its outcome in Strings.resx."),
        };

        return string.Join(" ", sentences.OfType<string>());
    }

    /// <summary>What execution did with it, with why nothing was sent when that's known.</summary>
    public static string ResultOf(ExecutionResult result) => result.Reason is { } reason
        ? ExecutionReasonOf(reason)
        : ExecutionOf(result.Status);

    /// <summary>The trade in a few words, such as "Buy BTCUSDT", for a button's label.</summary>
    public static string TradeOf(ApprovalProposal proposal) =>
        string.Format(CultureInfo.CurrentCulture, Strings.ApprovalTradeOf, ActionOf(proposal.Action), proposal.Symbol);

    /// <summary>An amount with its asset, such as "60,123.5 USDT", without trailing zeros (DATA-R-02).</summary>
    public static string AmountOf(decimal amount, string asset) =>
        string.Format(CultureInfo.CurrentCulture, Strings.AgentCapitalOf, Number(amount), asset);

    /// <summary>Without trailing zeros, with the user's separators.</summary>
    public static string Number(decimal value) => value.ToString("#,0.############################", CultureInfo.CurrentCulture);

    /// <summary>An approval field's name on the activity page, or null for a field that isn't an approval's.</summary>
    public static string? AuditFieldOf(string field) => field switch
    {
        ApprovalAuditFields.Action => Strings.ActivityFieldTradeAction,
        ApprovalAuditFields.Status => Strings.ActivityFieldApprovalStatus,
        _ => null,
    };

    /// <summary>An approval field's value on the activity page, or null when it's shown as recorded.</summary>
    public static string? AuditValueOf(string field, string value) => field switch
    {
        ApprovalAuditFields.Action when Parse<IntentAction>(value) is { } action => ActionOf(action),
        ApprovalAuditFields.Status when Parse<ApprovalStatus>(value) is { } status => StatusOf(status),
        _ => null,
    };

    /// <summary>Only exact names count, as on the rest of the activity page.</summary>
    private static T? Parse<T>(string value)
        where T : struct, Enum =>
        Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<T>(value) : null;
}
