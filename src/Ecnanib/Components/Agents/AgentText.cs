using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Components.Agents;

/// <summary>
/// What an agent's settings, statuses, problems and outcomes are called. Public so that a test can prove each
/// value has text: an issue that adds one adds its text here, or the page fails.
/// </summary>
public static class AgentText
{
    public static string StatusOf(AgentStatus status) => status switch
    {
        AgentStatus.Draft => Strings.AgentStatusDraft,
        AgentStatus.Active => Strings.AgentStatusActive,
        AgentStatus.Paused => Strings.AgentStatusPaused,
        AgentStatus.Stopped => Strings.AgentStatusStopped,
        AgentStatus.Error => Strings.AgentStatusError,
        AgentStatus.Archived => Strings.AgentStatusArchived,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Give the status its text in Strings.resx."),
    };

    /// <summary>Why an agent is in its status, as a short value for the activity page.</summary>
    public static string ReasonOf(AgentStatusReason reason) => reason switch
    {
        AgentStatusReason.User => Strings.AgentReasonUser,
        AgentStatusReason.CredentialDeleted => Strings.AgentReasonCredentialDeleted,
        AgentStatusReason.CredentialInvalid => Strings.AgentReasonCredentialInvalid,
        AgentStatusReason.MarketNotEnabled => Strings.AgentReasonMarketNotEnabled,
        AgentStatusReason.AiKeyUnavailable => Strings.AgentReasonAiKeyUnavailable,
        AgentStatusReason.ModelRetired => Strings.AgentReasonModelRetired,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Give the reason its text in Strings.resx."),
    };

    /// <summary>What the card says about a status the system chose, and what to do about it. Null when the user chose it.</summary>
    public static string? ExplanationOf(AgentStatusReason reason) => reason switch
    {
        AgentStatusReason.User => null,
        AgentStatusReason.CredentialDeleted => Strings.AgentExplanationCredentialDeleted,
        AgentStatusReason.CredentialInvalid => Strings.AgentExplanationCredentialInvalid,
        AgentStatusReason.MarketNotEnabled => Strings.AgentExplanationMarketNotEnabled,
        AgentStatusReason.AiKeyUnavailable => Strings.AgentExplanationAiKeyUnavailable,
        AgentStatusReason.ModelRetired => Strings.AgentExplanationModelRetired,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Give the reason its explanation in Strings.resx."),
    };

    public static string MarketOf(Market market) => market switch
    {
        Market.Spot => Strings.AgentMarketSpot,
        Market.Futures => Strings.AgentMarketFutures,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "Give the market its text in Strings.resx."),
    };

    public static string TimeframeOf(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => Strings.TimeframeFifteenMinutes,
        Timeframe.OneHour => Strings.TimeframeOneHour,
        Timeframe.FourHours => Strings.TimeframeFourHours,
        Timeframe.OneDay => Strings.TimeframeOneDay,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Give the timeframe its text in Strings.resx."),
    };

    public static string ExecutionModeOf(ExecutionMode mode) => mode switch
    {
        ExecutionMode.Autonomous => Strings.ExecutionModeAutonomous,
        ExecutionMode.Confirm => Strings.ExecutionModeConfirm,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Give the execution mode its text in Strings.resx."),
    };

    public static string RoleOf(AiRole role) => role switch
    {
        AiRole.TechnicalAnalyst => Strings.RoleTechnicalAnalyst,
        AiRole.SentimentAnalyst => Strings.RoleSentimentAnalyst,
        AiRole.Strategist => Strings.RoleStrategist,
        AiRole.RiskCritic => Strings.RoleRiskCritic,
        AiRole.Reviewer => Strings.RoleReviewer,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Give the role its text in Strings.resx."),
    };

    public static string RiskSettingOf(RiskSetting setting) => setting switch
    {
        RiskSetting.MaxRiskPerTradePercent => Strings.RiskMaxRiskPerTradePercent,
        RiskSetting.MaxPositionNotional => Strings.RiskMaxPositionNotional,
        RiskSetting.MaxLeverage => Strings.RiskMaxLeverage,
        RiskSetting.DailyLossLimitPercent => Strings.RiskDailyLossLimitPercent,
        RiskSetting.WeeklyLossLimitPercent => Strings.RiskWeeklyLossLimitPercent,
        RiskSetting.ConsecutiveLossesBeforePause => Strings.RiskConsecutiveLossesBeforePause,
        RiskSetting.MinRewardToRisk => Strings.RiskMinRewardToRisk,
        RiskSetting.MaxNewEntriesPerDay => Strings.RiskMaxNewEntriesPerDay,
        RiskSetting.MinStopDistanceAtr => Strings.RiskMinStopDistanceAtr,
        RiskSetting.MaxStopDistanceAtr => Strings.RiskMaxStopDistanceAtr,
        RiskSetting.LiquidationBufferMultiple => Strings.RiskLiquidationBufferMultiple,
        RiskSetting.FundingFilterPercent => Strings.RiskFundingFilterPercent,
        RiskSetting.CooldownCyclesAfterLoss => Strings.RiskCooldownCyclesAfterLoss,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Give the setting its text in Strings.resx."),
    };

    /// <summary>A line of plain-language help for each risk setting (NFR-USA-03).</summary>
    public static string RiskHintOf(RiskSetting setting) => setting switch
    {
        RiskSetting.MaxRiskPerTradePercent => Strings.RiskMaxRiskPerTradePercentHint,
        RiskSetting.MaxPositionNotional => Strings.RiskMaxPositionNotionalHint,
        RiskSetting.MaxLeverage => Strings.RiskMaxLeverageHint,
        RiskSetting.DailyLossLimitPercent => Strings.RiskDailyLossLimitPercentHint,
        RiskSetting.WeeklyLossLimitPercent => Strings.RiskWeeklyLossLimitPercentHint,
        RiskSetting.ConsecutiveLossesBeforePause => Strings.RiskConsecutiveLossesBeforePauseHint,
        RiskSetting.MinRewardToRisk => Strings.RiskMinRewardToRiskHint,
        RiskSetting.MaxNewEntriesPerDay => Strings.RiskMaxNewEntriesPerDayHint,
        RiskSetting.MinStopDistanceAtr => Strings.RiskMinStopDistanceAtrHint,
        RiskSetting.MaxStopDistanceAtr => Strings.RiskMaxStopDistanceAtrHint,
        RiskSetting.LiquidationBufferMultiple => Strings.RiskLiquidationBufferMultipleHint,
        RiskSetting.FundingFilterPercent => Strings.RiskFundingFilterPercentHint,
        RiskSetting.CooldownCyclesAfterLoss => Strings.RiskCooldownCyclesAfterLossHint,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Give the setting its hint in Strings.resx."),
    };

    /// <summary>What's wrong with one field of the settings form.</summary>
    public static string FieldErrorOf(AgentField field) => field switch
    {
        AgentField.Name => string.Format(CultureInfo.CurrentCulture, Strings.AgentFieldNameError, AgentLimits.NameMaxLength),
        AgentField.Description => string.Format(CultureInfo.CurrentCulture, Strings.AgentFieldDescriptionError, AgentLimits.DescriptionMaxLength),
        AgentField.Market => Strings.AgentFieldMarketError,
        AgentField.Environment => Strings.AgentFieldEnvironmentError,
        AgentField.Symbol => Strings.AgentFieldSymbolError,
        AgentField.Timeframe => Strings.AgentFieldTimeframeError,
        AgentField.ExchangeCredential => Strings.AgentFieldExchangeKeyError,
        AgentField.Roles => Strings.AgentFieldRolesError,
        AgentField.ExecutionMode => Strings.AgentFieldExecutionModeError,
        AgentField.CapitalAllocation => Strings.AgentFieldCapitalError,
        AgentField.Risk => Strings.AgentFieldRiskError,
        AgentField.Budget => Strings.AgentFieldBudgetError,
        AgentField.ReflectionsInPrompt =>
            string.Format(CultureInfo.CurrentCulture, Strings.AgentFieldReflectionsError, AgentLimits.MaxReflectionsInPrompt),
        AgentField.CustomInstructions =>
            string.Format(CultureInfo.CurrentCulture, Strings.AgentFieldCustomInstructionsError, AgentLimits.CustomInstructionsMaxLength),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Give the field its error in Strings.resx."),
    };

    /// <summary>Why a save was refused, or null when it was saved.</summary>
    public static string? SaveErrorOf(AgentSaveOutcome outcome) => outcome switch
    {
        AgentSaveOutcome.Saved => null,
        AgentSaveOutcome.Invalid => Strings.AgentSaveInvalid,
        AgentSaveOutcome.NameTaken => Strings.AgentSaveNameTaken,
        AgentSaveOutcome.LimitReached => Strings.AgentSaveLimitReached,
        AgentSaveOutcome.SymbolUnknown => Strings.AgentSymbolUnknown,
        AgentSaveOutcome.SymbolNotTrading => Strings.AgentSymbolNotTrading,
        AgentSaveOutcome.SymbolNotPerpetual => Strings.AgentSymbolNotPerpetual,
        AgentSaveOutcome.SymbolUnverifiable => Strings.AgentSymbolUnverifiable,
        AgentSaveOutcome.LiveNotAvailable => Strings.AgentLiveNotAvailable,
        AgentSaveOutcome.LockedByOpenPositions => Strings.AgentSaveLocked,
        AgentSaveOutcome.WouldFailActivation => Strings.AgentSaveWouldFailActivation,
        AgentSaveOutcome.SlotTaken => Strings.AgentSaveSlotTaken,
        AgentSaveOutcome.Archived => Strings.AgentSaveArchived,
        AgentSaveOutcome.NotFound => Strings.AgentGone,
        AgentSaveOutcome.Changed => Strings.AgentSaveChanged,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Give the outcome its text in Strings.resx."),
    };

    /// <summary>Why a change of status didn't happen, or null when it did.</summary>
    public static string? TransitionErrorOf(TransitionOutcome outcome) => outcome switch
    {
        TransitionOutcome.Done => null,
        TransitionOutcome.NotAllowed => Strings.AgentNotAllowed,
        TransitionOutcome.OpenPositions => Strings.AgentOpenPositions,
        TransitionOutcome.NotFound => Strings.AgentGone,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Give the outcome its text in Strings.resx."),
    };

    /// <summary>What has to be fixed before an agent can be activated.</summary>
    public static string ProblemOf(ActivationProblem problem) => problem.Kind switch
    {
        ActivationProblemKind.LiveNotAvailable => Strings.AgentLiveNotAvailable,
        ActivationProblemKind.NoExchangeKey => Strings.AgentProblemNoExchangeKey,
        ActivationProblemKind.ExchangeKeyMissing => Strings.AgentProblemExchangeKeyMissing,
        ActivationProblemKind.ExchangeKeyNotValid => Strings.AgentProblemExchangeKeyNotValid,
        ActivationProblemKind.MarketNotEnabled => Strings.AgentProblemMarketNotEnabled,
        ActivationProblemKind.SymbolUnknown => Strings.AgentSymbolUnknown,
        ActivationProblemKind.SymbolNotTrading => Strings.AgentSymbolNotTrading,
        ActivationProblemKind.SymbolNotPerpetual => Strings.AgentSymbolNotPerpetual,
        ActivationProblemKind.SymbolUnverifiable => Strings.AgentSymbolUnverifiable,
        ActivationProblemKind.RoleUnassigned => WithRole(Strings.AgentProblemRoleUnassigned, problem.Role),
        ActivationProblemKind.ModelNotActive => WithRole(Strings.AgentProblemModelNotActive, problem.Role),
        ActivationProblemKind.ModelNotAccessible => WithRole(Strings.AgentProblemModelNotAccessible, problem.Role),
        ActivationProblemKind.ModelLacksStructuredOutput => WithRole(Strings.AgentProblemModelLacksStructuredOutput, problem.Role),
        ActivationProblemKind.NoCapitalAllocation => Strings.AgentProblemNoCapitalAllocation,
        ActivationProblemKind.RiskSettingsInvalid => Strings.AgentProblemRiskSettingsInvalid,
        ActivationProblemKind.SlotTaken => string.Format(CultureInfo.CurrentCulture, Strings.AgentProblemSlotTaken, problem.OtherAgent),
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem.Kind, "Give the problem its text in Strings.resx."),
    };

    /// <summary>What's worth knowing about an agent that was, or can be, activated.</summary>
    public static string WarningOf(ActivationWarning warning) => warning.Kind switch
    {
        ActivationWarningKind.OptionalModelUnavailable => WithRole(Strings.AgentWarningOptionalModelUnavailable, warning.Role),
        ActivationWarningKind.FallbackUnavailable => WithRole(Strings.AgentWarningFallbackUnavailable, warning.Role),
        ActivationWarningKind.OptionalModelLacksStructuredOutput => WithRole(Strings.AgentWarningOptionalModelLacksStructuredOutput, warning.Role),
        ActivationWarningKind.FallbackLacksStructuredOutput => WithRole(Strings.AgentWarningFallbackLacksStructuredOutput, warning.Role),
        ActivationWarningKind.RiskCriticSharesProvider => Strings.AgentWarningRiskCriticSharesProvider,
        ActivationWarningKind.SentimentWithoutLiveSearch => Strings.AgentWarningSentimentWithoutLiveSearch,
        ActivationWarningKind.CapitalExceedsBalance => string.Format(
            CultureInfo.CurrentCulture, Strings.AgentWarningCapitalExceedsBalance, warning.Allocated, warning.Available, warning.Asset),
        _ => throw new ArgumentOutOfRangeException(nameof(warning), warning.Kind, "Give the warning its text in Strings.resx."),
    };

    /// <summary>
    /// What an agent's audit field is called on the activity page, or null for one that isn't an agent's.
    /// The fields agents share with credentials, Environment and Status, take the credentials' text first.
    /// </summary>
    public static string? AuditFieldOf(string field)
    {
        if (Suffix<AiRole>(field, AgentAuditFields.RolePrefix) is { } role)
        {
            return RoleOf(role);
        }

        if (Suffix<AiRole>(field, AgentAuditFields.FallbackPrefix) is { } fallback)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.ActivityFieldFallback, RoleOf(fallback));
        }

        if (Suffix<RiskSetting>(field, AgentAuditFields.RiskPrefix) is { } setting)
        {
            return RiskSettingOf(setting);
        }

        return field switch
        {
            AgentAuditFields.Name => Strings.ActivityFieldAgentName,
            AgentAuditFields.Market => Strings.ActivityFieldMarket,
            AgentAuditFields.Environment => Strings.ActivityFieldEnvironment,
            AgentAuditFields.Symbol => Strings.ActivityFieldPair,
            AgentAuditFields.Timeframe => Strings.ActivityFieldTimeframe,
            AgentAuditFields.ExchangeKey => Strings.ActivityFieldExchangeKey,
            AgentAuditFields.ExecutionMode => Strings.ActivityFieldExecutionMode,
            AgentAuditFields.AutoExecuteRiskReducing => Strings.ActivityFieldAutoExecuteRiskReducing,
            AgentAuditFields.CapitalAllocation => Strings.ActivityFieldCapitalAllocation,
            AgentAuditFields.DailyBudget => Strings.ActivityFieldDailyBudget,
            AgentAuditFields.MonthlyBudget => Strings.ActivityFieldMonthlyBudget,
            AgentAuditFields.DailyTokenCap => Strings.ActivityFieldDailyTokenCap,
            AgentAuditFields.MonthlyTokenCap => Strings.ActivityFieldMonthlyTokenCap,
            AgentAuditFields.ExperienceMemory => Strings.ActivityFieldExperienceMemory,
            AgentAuditFields.ReflectionsInPrompt => Strings.ActivityFieldReflectionsInPrompt,
            AgentAuditFields.Status => Strings.ActivityFieldStatus,
            AgentAuditFields.Reason => Strings.ActivityFieldReason,
            AgentAuditFields.ClonedFrom => Strings.ActivityFieldClonedFrom,
            _ => null,
        };
    }

    /// <summary>An agent field's value as the activity page shows it, or null when it's shown as recorded.</summary>
    public static string? AuditValueOf(string field, string value) => field switch
    {
        AgentAuditFields.Market when Parse<Market>(value) is { } market => MarketOf(market),
        AgentAuditFields.Timeframe when Parse<Timeframe>(value) is { } timeframe => TimeframeOf(timeframe),
        AgentAuditFields.ExecutionMode when Parse<ExecutionMode>(value) is { } mode => ExecutionModeOf(mode),
        AgentAuditFields.Status when Parse<AgentStatus>(value) is { } status => StatusOf(status),
        AgentAuditFields.Reason when Parse<AgentStatusReason>(value) is { } reason => ReasonOf(reason),
        AgentAuditFields.AutoExecuteRiskReducing or AgentAuditFields.ExperienceMemory => OnOff(value),
        _ => null,
    };

    /// <summary>A model's option in the settings form: its name, then its provider.</summary>
    public static string ModelOption(string displayName, string provider) =>
        string.Format(CultureInfo.CurrentCulture, Strings.AgentSettingsModelOption, displayName, provider);

    /// <summary>A model's option for a role that prefers live search, which says whether it has it.</summary>
    public static string ModelOption(ModelChoice choice, AiRole role) =>
        string.Format(
            CultureInfo.CurrentCulture,
            role == AiRole.SentimentAnalyst && choice.LiveSearch == true ? Strings.AgentSettingsModelOptionLiveSearch : Strings.AgentSettingsModelOption,
            choice.DisplayName,
            choice.Model.Provider);

    private static string? OnOff(string value) => value switch
    {
        "True" => Strings.AgentOn,
        "False" => Strings.AgentOff,
        _ => null,
    };

    private static string WithRole(string format, AiRole? role) =>
        string.Format(CultureInfo.CurrentCulture, format, role is { } known ? RoleOf(known) : "");

    /// <summary>An exact name after the prefix, because Enum.TryParse also takes a number.</summary>
    private static T? Suffix<T>(string field, string prefix)
        where T : struct, Enum =>
        field.StartsWith(prefix, StringComparison.Ordinal) ? Parse<T>(field[prefix.Length..]) : null;

    private static T? Parse<T>(string value)
        where T : struct, Enum =>
        Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<T>(value) : null;
}
