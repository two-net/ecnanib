using System.Globalization;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Components.Traces;

/// <summary>
/// What the decision trace viewer calls the Risk Engine's rules and results, the council's calls, the Futures setup and
/// orders. Public so that a test can prove each value has text: an issue that adds one adds its text here, or the page
/// fails. The approval's and execution's text is <see cref="Approvals.ApprovalText"/>'s, and the agent's
/// <see cref="Agents.AgentText"/>'s.
/// </summary>
public static class TraceText
{
    public static string RuleOf(RiskRule rule) => rule switch
    {
        RiskRule.Settings => Strings.TraceRuleSettings,
        RiskRule.Action => Strings.TraceRuleAction,
        RiskRule.SymbolTrading => Strings.TraceRuleSymbolTrading,
        RiskRule.DailyLoss => Strings.TraceRuleDailyLoss,
        RiskRule.WeeklyLoss => Strings.TraceRuleWeeklyLoss,
        RiskRule.ConsecutiveLosses => Strings.TraceRuleConsecutiveLosses,
        RiskRule.EntriesPerDay => Strings.TraceRuleEntriesPerDay,
        RiskRule.Cooldown => Strings.TraceRuleCooldown,
        RiskRule.PendingEntry => Strings.TraceRulePendingEntry,
        RiskRule.Funding => Strings.TraceRuleFunding,
        RiskRule.Prices => Strings.TraceRulePrices,
        RiskRule.StopRequired => Strings.TraceRuleStopRequired,
        RiskRule.StopSide => Strings.TraceRuleStopSide,
        RiskRule.StopDistance => Strings.TraceRuleStopDistance,
        RiskRule.StopNotWidened => Strings.TraceRuleStopNotWidened,
        RiskRule.TargetSide => Strings.TraceRuleTargetSide,
        RiskRule.RewardToRisk => Strings.TraceRuleRewardToRisk,
        RiskRule.NoAveragingDown => Strings.TraceRuleNoAveragingDown,
        RiskRule.Size => Strings.TraceRuleSize,
        RiskRule.Quantity => Strings.TraceRuleQuantity,
        RiskRule.Notional => Strings.TraceRuleNotional,
        RiskRule.LiquidationBuffer => Strings.TraceRuleLiquidationBuffer,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Give the rule its text in Strings.resx."),
    };

    /// <summary>In words, so a result never relies on color (FR-ANI-03).</summary>
    public static string RuleOutcomeOf(RuleOutcome outcome) => outcome switch
    {
        RuleOutcome.Pass => Strings.TraceRuleOutcomePass,
        RuleOutcome.Fail => Strings.TraceRuleOutcomeFail,
        RuleOutcome.NotApplicable => Strings.TraceRuleOutcomeNotApplicable,
        RuleOutcome.NotEvaluated => Strings.TraceRuleOutcomeNotEvaluated,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Give the outcome its text in Strings.resx."),
    };

    public static string VerdictOf(RiskVerdict verdict) => verdict switch
    {
        RiskVerdict.Pass => Strings.TraceVerdictPass,
        RiskVerdict.Fail => Strings.TraceVerdictFail,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Give the verdict its text in Strings.resx."),
    };

    public static string LossLimitOf(LossLimitKind kind) => kind switch
    {
        LossLimitKind.Daily => Strings.TraceLossLimitDaily,
        LossLimitKind.Weekly => Strings.TraceLossLimitWeekly,
        LossLimitKind.ConsecutiveLosses => Strings.TraceLossLimitConsecutiveLosses,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Give the loss limit its text in Strings.resx."),
    };

    public static string OriginOf(IntentOrigin origin) => origin switch
    {
        IntentOrigin.Council => Strings.TraceOriginCouncil,
        IntentOrigin.System => Strings.TraceOriginSystem,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Give the origin its text in Strings.resx."),
    };

    public static string EntryTypeOf(EntryType type) => type switch
    {
        EntryType.Market => Strings.TraceEntryMarket,
        EntryType.Limit => Strings.TraceEntryLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Give the entry type its text in Strings.resx."),
    };

    public static string SizedKindOf(SizedOrderKind kind) => kind switch
    {
        SizedOrderKind.Entry => Strings.TraceSizedEntry,
        SizedOrderKind.Exit => Strings.TraceSizedExit,
        SizedOrderKind.StopAdjustment => Strings.TraceSizedStopAdjustment,
        SizedOrderKind.TargetAdjustment => Strings.TraceSizedTargetAdjustment,
        SizedOrderKind.CancelEntry => Strings.TraceSizedCancelEntry,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Give the kind its text in Strings.resx."),
    };

    public static string SideOf(OrderSide side) => side switch
    {
        OrderSide.Buy => Strings.TraceSideBuy,
        OrderSide.Sell => Strings.TraceSideSell,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Give the side its text in Strings.resx."),
    };

    /// <summary>What the Strategist proposed, on a line of its own, as the decision timeline lists it.</summary>
    public static string ProposalOf(TradeAction action) => action switch
    {
        TradeAction.Hold => Strings.TraceProposalHold,
        TradeAction.Buy => Strings.TraceProposalBuy,
        TradeAction.Sell => Strings.TraceProposalSell,
        TradeAction.OpenLong => Strings.TraceProposalOpenLong,
        TradeAction.OpenShort => Strings.TraceProposalOpenShort,
        TradeAction.Close => Strings.TraceProposalClose,
        TradeAction.AdjustStop => Strings.TraceProposalAdjustStop,
        TradeAction.AdjustTarget => Strings.TraceProposalAdjustTarget,
        TradeAction.CancelPending => Strings.TraceProposalCancelPending,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Give the action its text in Strings.resx."),
    };

    public static string BudgetLimitOf(BudgetLimit limit) => limit switch
    {
        BudgetLimit.DailyUsd => Strings.TraceBudgetLimitDailyUsd,
        BudgetLimit.MonthlyUsd => Strings.TraceBudgetLimitMonthlyUsd,
        BudgetLimit.DailyTokens => Strings.TraceBudgetLimitDailyTokens,
        BudgetLimit.MonthlyTokens => Strings.TraceBudgetLimitMonthlyTokens,
        _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, "Give the limit its text in Strings.resx."),
    };

    /// <summary>Why a cycle ended without a decision, as a sentence.</summary>
    public static string FailureOf(CycleFailure failure) => failure switch
    {
        CycleFailure.MarketData => Strings.TraceFailureMarketData,
        CycleFailure.Account => Strings.TraceFailureAccount,
        CycleFailure.RoleFailed => Strings.TraceFailureRoleFailed,
        CycleFailure.Unexpected => Strings.TraceFailureUnexpected,
        CycleFailure.Execution => Strings.TraceFailureExecution,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Give the failure its text in Strings.resx."),
    };

    /// <summary>FR-ROLE-08's states, in words.</summary>
    public static string RoleStateOf(RoleState state) => state switch
    {
        RoleState.Queued => Strings.TraceRoleQueued,
        RoleState.Running => Strings.TraceRoleRunning,
        RoleState.Done => Strings.TraceRoleDone,
        RoleState.Failed => Strings.TraceRoleFailed,
        RoleState.Skipped => Strings.TraceRoleSkipped,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Give the state its text in Strings.resx."),
    };

    public static string CallStatusOf(CouncilCallStatus status) => status switch
    {
        CouncilCallStatus.Answered => Strings.TraceCallAnswered,
        CouncilCallStatus.BrokeContract => Strings.TraceCallBrokeContract,
        CouncilCallStatus.Failed => Strings.TraceCallFailed,
        CouncilCallStatus.NotSent => Strings.TraceCallNotSent,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Give the status its text in Strings.resx."),
    };

    /// <summary>Why a call failed or wasn't sent, as a sentence.</summary>
    public static string CallFailureOf(CouncilFailure failure) => failure switch
    {
        CouncilFailure.NoKey => Strings.TraceCallFailureNoKey,
        CouncilFailure.KeyUnusable => Strings.TraceCallFailureKeyUnusable,
        CouncilFailure.TemplateMissing => Strings.TraceCallFailureTemplateMissing,
        CouncilFailure.Authentication => Strings.TraceCallFailureAuthentication,
        CouncilFailure.RateLimit => Strings.TraceCallFailureRateLimit,
        CouncilFailure.Overloaded => Strings.TraceCallFailureOverloaded,
        CouncilFailure.InvalidRequest => Strings.TraceCallFailureInvalidRequest,
        CouncilFailure.Refusal => Strings.TraceCallFailureRefusal,
        CouncilFailure.Timeout => Strings.TraceCallFailureTimeout,
        CouncilFailure.Other => Strings.TraceCallFailureOther,
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Give the failure its text in Strings.resx."),
    };

    public static string LayerOf(PromptLayer layer) => layer switch
    {
        PromptLayer.System => Strings.TraceLayerSystem,
        PromptLayer.OutputContract => Strings.TraceLayerOutputContract,
        PromptLayer.Role => Strings.TraceLayerRole,
        PromptLayer.Market => Strings.TraceLayerMarket,
        PromptLayer.Timeframe => Strings.TraceLayerTimeframe,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Give the layer its text in Strings.resx."),
    };

    /// <summary>What readying a Futures symbol found, as a sentence.</summary>
    public static string FuturesSetupOf(FuturesSetupOutcome outcome) => outcome switch
    {
        FuturesSetupOutcome.Ready => Strings.TraceFuturesReady,
        FuturesSetupOutcome.KeyUnusable => Strings.TraceFuturesKeyUnusable,
        FuturesSetupOutcome.ExchangeUnavailable => Strings.TraceFuturesExchangeUnavailable,
        FuturesSetupOutcome.TradingDisabled => Strings.TraceFuturesTradingDisabled,
        FuturesSetupOutcome.HedgeMode => Strings.TraceFuturesHedgeMode,
        FuturesSetupOutcome.MultiAssetsMode => Strings.TraceFuturesMultiAssetsMode,
        FuturesSetupOutcome.MarginTypeLocked => Strings.TraceFuturesMarginTypeLocked,
        FuturesSetupOutcome.LeverageRefused => Strings.TraceFuturesLeverageRefused,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Give the outcome its text in Strings.resx."),
    };

    public static string LegOf(OrderLeg leg) => leg switch
    {
        OrderLeg.Entry => Strings.TraceLegEntry,
        OrderLeg.Stop => Strings.TraceLegStop,
        OrderLeg.Target => Strings.TraceLegTarget,
        OrderLeg.EmergencyClose => Strings.TraceLegEmergencyClose,
        OrderLeg.Exit => Strings.TraceLegExit,
        _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "Give the leg its text in Strings.resx."),
    };

    public static string OrderKindOf(OrderKind kind) => kind switch
    {
        OrderKind.Market => Strings.TraceOrderKindMarket,
        OrderKind.Limit => Strings.TraceOrderKindLimit,
        OrderKind.StopMarket => Strings.TraceOrderKindStopMarket,
        OrderKind.TakeProfitMarket => Strings.TraceOrderKindTakeProfitMarket,
        OrderKind.LimitMaker => Strings.TraceOrderKindLimitMaker,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Give the order type its text in Strings.resx."),
    };

    public static string OrderStatusOf(OrderStatus status) => status switch
    {
        OrderStatus.Sending => Strings.TraceOrderSending,
        OrderStatus.Unknown => Strings.TraceOrderUnknown,
        OrderStatus.NotPlaced => Strings.TraceOrderNotPlaced,
        OrderStatus.Open => Strings.TraceOrderOpen,
        OrderStatus.PartiallyFilled => Strings.TraceOrderPartiallyFilled,
        OrderStatus.Filled => Strings.TraceOrderFilled,
        OrderStatus.Triggered => Strings.TraceOrderTriggered,
        OrderStatus.Finished => Strings.TraceOrderFinished,
        OrderStatus.Canceled => Strings.TraceOrderCanceled,
        OrderStatus.Expired => Strings.TraceOrderExpired,
        OrderStatus.Rejected => Strings.TraceOrderRejected,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Give the status its text in Strings.resx."),
    };

    /// <summary>A setting's value from the settings a rule used, as a number.</summary>
    public static decimal? SettingOf(RiskSettings settings, RiskSetting setting)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return setting switch
        {
            RiskSetting.MaxRiskPerTradePercent => settings.MaxRiskPerTradePercent,
            RiskSetting.MaxPositionNotional => settings.MaxPositionNotional,
            RiskSetting.MaxLeverage => settings.MaxLeverage,
            RiskSetting.DailyLossLimitPercent => settings.DailyLossLimitPercent,
            RiskSetting.WeeklyLossLimitPercent => settings.WeeklyLossLimitPercent,
            RiskSetting.ConsecutiveLossesBeforePause => settings.ConsecutiveLossesBeforePause,
            RiskSetting.MinRewardToRisk => settings.MinRewardToRisk,
            RiskSetting.MaxNewEntriesPerDay => settings.MaxNewEntriesPerDay,
            RiskSetting.MinStopDistanceAtr => settings.MinStopDistanceAtr,
            RiskSetting.MaxStopDistanceAtr => settings.MaxStopDistanceAtr,
            RiskSetting.LiquidationBufferMultiple => settings.LiquidationBufferMultiple,
            RiskSetting.FundingFilterPercent => settings.FundingFilterPercent,
            RiskSetting.CooldownCyclesAfterLoss => settings.CooldownCyclesAfterLoss,
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Give the setting its value here."),
        };
    }

    /// <summary>
    /// A cost in US dollars to the cent and below, down to a millionth, because one call can cost a fraction of a cent.
    /// A model without a price says so.
    /// </summary>
    public static string CostOf(decimal? usd) => usd is { } cost
        ? string.Format(
            CultureInfo.CurrentCulture,
            Strings.AgentUsdOf,
            Math.Round(cost, cost >= 1m ? 2 : 6, MidpointRounding.AwayFromZero).ToString(cost >= 1m ? "#,0.00" : "#,0.######", CultureInfo.CurrentCulture))
        : Strings.TraceCostUnpriced;

    /// <summary>A duration in seconds, such as "2.4 s".</summary>
    public static string SecondsOf(TimeSpan duration) =>
        string.Format(CultureInfo.CurrentCulture, Strings.TraceSecondsOf, duration.TotalSeconds);

    /// <summary>A count, such as of tokens, with the user's separators.</summary>
    public static string CountOf(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    public static string TokensOf(long input, long output) =>
        string.Format(CultureInfo.CurrentCulture, Strings.TraceTokensOf, input, output);

    /// <summary>A price, a quantity or an indicator as recorded, without trailing zeros, or "None" when there isn't one.</summary>
    public static string NumberOf(decimal? value) => value is { } number ? Agents.AgentText.Number(number) : Strings.TraceNone;
}
