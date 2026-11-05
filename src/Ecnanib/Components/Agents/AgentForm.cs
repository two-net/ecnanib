using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Components.Agents;

/// <summary>
/// The settings form, as posted. Every value is a string, so that one nobody offered is a refusal rather than
/// a failure to bind. <see cref="ToSettings"/> turns it into <see cref="AgentSettings"/>, and says which fields
/// it couldn't read. The contract checks everything else.
/// </summary>
internal sealed class AgentForm
{
    /// <summary>Separates a model's provider from its ID in a select's value. No provider's name has one.</summary>
    public const char ModelSeparator = '|';

    public string? Version { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Market { get; set; }

    public string? Symbol { get; set; }

    public string? Timeframe { get; set; }

    public string? ExchangeKey { get; set; }

    public string? ExecutionMode { get; set; }

    /// <summary>"true" when ticked. A checkbox that isn't ticked sends nothing.</summary>
    public string? AutoExecuteRiskReducing { get; set; }

    public string? TechnicalAnalyst { get; set; }

    public string? TechnicalAnalystFallback { get; set; }

    public string? SentimentAnalyst { get; set; }

    public string? SentimentAnalystFallback { get; set; }

    public string? Strategist { get; set; }

    public string? StrategistFallback { get; set; }

    public string? RiskCritic { get; set; }

    public string? RiskCriticFallback { get; set; }

    public string? Reviewer { get; set; }

    public string? ReviewerFallback { get; set; }

    public string? CapitalAllocation { get; set; }

    public string? MaxRiskPerTradePercent { get; set; }

    public string? MaxPositionNotional { get; set; }

    public string? MaxLeverage { get; set; }

    public string? DailyLossLimitPercent { get; set; }

    public string? WeeklyLossLimitPercent { get; set; }

    public string? ConsecutiveLossesBeforePause { get; set; }

    public string? MinRewardToRisk { get; set; }

    public string? MaxNewEntriesPerDay { get; set; }

    public string? MinStopDistanceAtr { get; set; }

    public string? MaxStopDistanceAtr { get; set; }

    public string? LiquidationBufferMultiple { get; set; }

    public string? FundingFilterPercent { get; set; }

    public string? CooldownCyclesAfterLoss { get; set; }

    public string? DailyBudgetUsd { get; set; }

    public string? MonthlyBudgetUsd { get; set; }

    public string? DailyTokens { get; set; }

    public string? MonthlyTokens { get; set; }

    /// <summary>"true" when ticked.</summary>
    public string? ExperienceEnabled { get; set; }

    public string? ReflectionsInPrompt { get; set; }

    public string? CustomInstructions { get; set; }

    /// <summary>The form's value for a role's model, or its fallback.</summary>
    public string? RoleValue(AiRole role, bool fallback) => (role, fallback) switch
    {
        (AiRole.TechnicalAnalyst, false) => TechnicalAnalyst,
        (AiRole.TechnicalAnalyst, true) => TechnicalAnalystFallback,
        (AiRole.SentimentAnalyst, false) => SentimentAnalyst,
        (AiRole.SentimentAnalyst, true) => SentimentAnalystFallback,
        (AiRole.Strategist, false) => Strategist,
        (AiRole.Strategist, true) => StrategistFallback,
        (AiRole.RiskCritic, false) => RiskCritic,
        (AiRole.RiskCritic, true) => RiskCriticFallback,
        (AiRole.Reviewer, false) => Reviewer,
        (AiRole.Reviewer, true) => ReviewerFallback,
        _ => null,
    };

    /// <summary>The form's value for a risk setting.</summary>
    public string? RiskValue(RiskSetting setting) => setting switch
    {
        RiskSetting.MaxRiskPerTradePercent => MaxRiskPerTradePercent,
        RiskSetting.MaxPositionNotional => MaxPositionNotional,
        RiskSetting.MaxLeverage => MaxLeverage,
        RiskSetting.DailyLossLimitPercent => DailyLossLimitPercent,
        RiskSetting.WeeklyLossLimitPercent => WeeklyLossLimitPercent,
        RiskSetting.ConsecutiveLossesBeforePause => ConsecutiveLossesBeforePause,
        RiskSetting.MinRewardToRisk => MinRewardToRisk,
        RiskSetting.MaxNewEntriesPerDay => MaxNewEntriesPerDay,
        RiskSetting.MinStopDistanceAtr => MinStopDistanceAtr,
        RiskSetting.MaxStopDistanceAtr => MaxStopDistanceAtr,
        RiskSetting.LiquidationBufferMultiple => LiquidationBufferMultiple,
        RiskSetting.FundingFilterPercent => FundingFilterPercent,
        RiskSetting.CooldownCyclesAfterLoss => CooldownCyclesAfterLoss,
        _ => null,
    };

    /// <summary>A form holding <paramref name="settings"/>, as the page first shows them.</summary>
    public static AgentForm Of(AgentSettings settings, int? version)
    {
        var form = new AgentForm
        {
            Version = version?.ToString(CultureInfo.InvariantCulture),
            Name = settings.Name,
            Description = settings.Description,
            Market = settings.Market.ToString(),
            Symbol = settings.Symbol,
            Timeframe = settings.Timeframe.ToString(),
            ExchangeKey = settings.ExchangeCredentialId?.ToString(),
            ExecutionMode = settings.ExecutionMode.ToString(),
            AutoExecuteRiskReducing = settings.AutoExecuteRiskReducing ? "true" : null,
            CapitalAllocation = Number(settings.CapitalAllocation),
            DailyBudgetUsd = Number(settings.Budget.DailyUsd),
            MonthlyBudgetUsd = Number(settings.Budget.MonthlyUsd),
            DailyTokens = settings.Budget.DailyTokens?.ToString(CultureInfo.InvariantCulture),
            MonthlyTokens = settings.Budget.MonthlyTokens?.ToString(CultureInfo.InvariantCulture),
            ExperienceEnabled = settings.Experience.Enabled ? "true" : null,
            ReflectionsInPrompt = settings.Experience.ReflectionsInPrompt.ToString(CultureInfo.InvariantCulture),
            CustomInstructions = settings.CustomInstructions,
        };

        foreach (var assignment in settings.Roles)
        {
            form.SetRole(assignment.Role, false, ValueOf(assignment.Model));
            form.SetRole(assignment.Role, true, assignment.Fallback is { } fallback ? ValueOf(fallback) : null);
        }

        var risk = settings.Risk;
        form.MaxRiskPerTradePercent = Number(risk.MaxRiskPerTradePercent);
        form.MaxPositionNotional = Number(risk.MaxPositionNotional);
        form.MaxLeverage = risk.MaxLeverage.ToString(CultureInfo.InvariantCulture);
        form.DailyLossLimitPercent = Number(risk.DailyLossLimitPercent);
        form.WeeklyLossLimitPercent = Number(risk.WeeklyLossLimitPercent);
        form.ConsecutiveLossesBeforePause = risk.ConsecutiveLossesBeforePause.ToString(CultureInfo.InvariantCulture);
        form.MinRewardToRisk = Number(risk.MinRewardToRisk);
        form.MaxNewEntriesPerDay = risk.MaxNewEntriesPerDay.ToString(CultureInfo.InvariantCulture);
        form.MinStopDistanceAtr = Number(risk.MinStopDistanceAtr);
        form.MaxStopDistanceAtr = Number(risk.MaxStopDistanceAtr);
        form.LiquidationBufferMultiple = Number(risk.LiquidationBufferMultiple);
        form.FundingFilterPercent = Number(risk.FundingFilterPercent);
        form.CooldownCyclesAfterLoss = risk.CooldownCyclesAfterLoss.ToString(CultureInfo.InvariantCulture);

        return form;
    }

    /// <summary>A model as a select's value.</summary>
    public static string ValueOf(ModelRef model) => $"{model.Provider}{ModelSeparator}{model.ModelId}";

    /// <summary>
    /// The settings, or null with the fields that couldn't be read. A number that isn't one is refused here, and
    /// one out of range by the contract.
    /// </summary>
    public (AgentSettings? Settings, HashSet<AgentField> Fields, HashSet<RiskSetting> RiskSettings) ToSettings()
    {
        var fields = new HashSet<AgentField>();
        var riskSettings = new HashSet<RiskSetting>();

        var market = Parse<Market>(Market);
        var timeframe = Parse<Timeframe>(Timeframe);
        var mode = Parse<ExecutionMode>(ExecutionMode);

        Require(market is not null, AgentField.Market);
        Require(timeframe is not null, AgentField.Timeframe);
        Require(mode is not null, AgentField.ExecutionMode);

        Guid? exchangeKey = null;

        if (!string.IsNullOrEmpty(ExchangeKey))
        {
            Require(Guid.TryParse(ExchangeKey, out var key), AgentField.ExchangeCredential);
            exchangeKey = key;
        }

        var roles = new List<RoleAssignment>();

        foreach (var role in Enum.GetValues<AiRole>())
        {
            var model = Model(RoleValue(role, false), out var modelValid);
            var fallback = Model(RoleValue(role, true), out var fallbackValid);

            Require(modelValid && fallbackValid && (model is not null || fallback is null), AgentField.Roles);

            if (model is not null)
            {
                roles.Add(new RoleAssignment(role, model, fallback));
            }
        }

        var capital = OptionalDecimal(CapitalAllocation, out var capitalValid);
        Require(capitalValid, AgentField.CapitalAllocation);

        decimal RiskDecimal(RiskSetting setting, decimal fallback)
        {
            if (decimal.TryParse(RiskValue(setting), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            riskSettings.Add(setting);
            return fallback;
        }

        int RiskInteger(RiskSetting setting, int fallback)
        {
            if (int.TryParse(RiskValue(setting), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            riskSettings.Add(setting);
            return fallback;
        }

        var defaults = RiskSettings.Default;
        var notional = OptionalDecimal(MaxPositionNotional, out var notionalValid);

        if (!notionalValid)
        {
            riskSettings.Add(RiskSetting.MaxPositionNotional);
        }

        var risk = new RiskSettings
        {
            MaxRiskPerTradePercent = RiskDecimal(RiskSetting.MaxRiskPerTradePercent, defaults.MaxRiskPerTradePercent),
            MaxPositionNotional = notional,
            MaxLeverage = RiskInteger(RiskSetting.MaxLeverage, defaults.MaxLeverage),
            DailyLossLimitPercent = RiskDecimal(RiskSetting.DailyLossLimitPercent, defaults.DailyLossLimitPercent),
            WeeklyLossLimitPercent = RiskDecimal(RiskSetting.WeeklyLossLimitPercent, defaults.WeeklyLossLimitPercent),
            ConsecutiveLossesBeforePause = RiskInteger(RiskSetting.ConsecutiveLossesBeforePause, defaults.ConsecutiveLossesBeforePause),
            MinRewardToRisk = RiskDecimal(RiskSetting.MinRewardToRisk, defaults.MinRewardToRisk),
            MaxNewEntriesPerDay = RiskInteger(RiskSetting.MaxNewEntriesPerDay, defaults.MaxNewEntriesPerDay),
            MinStopDistanceAtr = RiskDecimal(RiskSetting.MinStopDistanceAtr, defaults.MinStopDistanceAtr),
            MaxStopDistanceAtr = RiskDecimal(RiskSetting.MaxStopDistanceAtr, defaults.MaxStopDistanceAtr),
            LiquidationBufferMultiple = RiskDecimal(RiskSetting.LiquidationBufferMultiple, defaults.LiquidationBufferMultiple),
            FundingFilterPercent = RiskDecimal(RiskSetting.FundingFilterPercent, defaults.FundingFilterPercent),
            CooldownCyclesAfterLoss = RiskInteger(RiskSetting.CooldownCyclesAfterLoss, defaults.CooldownCyclesAfterLoss),
        };

        Require(riskSettings.Count == 0, AgentField.Risk);

        var dailyUsd = OptionalDecimal(DailyBudgetUsd, out var dailyUsdValid);
        var monthlyUsd = OptionalDecimal(MonthlyBudgetUsd, out var monthlyUsdValid);
        var dailyTokens = OptionalLong(DailyTokens, out var dailyTokensValid);
        var monthlyTokens = OptionalLong(MonthlyTokens, out var monthlyTokensValid);
        Require(dailyUsdValid && monthlyUsdValid && dailyTokensValid && monthlyTokensValid, AgentField.Budget);

        var reflectionsValid = int.TryParse(ReflectionsInPrompt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reflections);
        Require(reflectionsValid, AgentField.ReflectionsInPrompt);

        if (fields.Count > 0)
        {
            return (null, fields, riskSettings);
        }

        var settings = new AgentSettings
        {
            Name = Name ?? "",
            Description = Description,
            Market = market!.Value,

            // Demo only, until switching to Live exists (#39).
            Environment = TradingEnvironment.Demo,
            Symbol = Symbol ?? "",
            Timeframe = timeframe!.Value,
            ExchangeCredentialId = exchangeKey,
            ExecutionMode = mode!.Value,
            AutoExecuteRiskReducing = AutoExecuteRiskReducing == "true",
            Roles = roles,
            CapitalAllocation = capital,
            Risk = risk,
            Budget = new AiBudget(dailyUsd, monthlyUsd, dailyTokens, monthlyTokens),
            Experience = new ExperienceMemory(ExperienceEnabled == "true", reflections),
            CustomInstructions = CustomInstructions,
        };

        return (settings, fields, riskSettings);

        void Require(bool valid, AgentField field)
        {
            if (!valid)
            {
                fields.Add(field);
            }
        }
    }

    private void SetRole(AiRole role, bool fallback, string? value)
    {
        switch (role, fallback)
        {
            case (AiRole.TechnicalAnalyst, false): TechnicalAnalyst = value; break;
            case (AiRole.TechnicalAnalyst, true): TechnicalAnalystFallback = value; break;
            case (AiRole.SentimentAnalyst, false): SentimentAnalyst = value; break;
            case (AiRole.SentimentAnalyst, true): SentimentAnalystFallback = value; break;
            case (AiRole.Strategist, false): Strategist = value; break;
            case (AiRole.Strategist, true): StrategistFallback = value; break;
            case (AiRole.RiskCritic, false): RiskCritic = value; break;
            case (AiRole.RiskCritic, true): RiskCriticFallback = value; break;
            case (AiRole.Reviewer, false): Reviewer = value; break;
            case (AiRole.Reviewer, true): ReviewerFallback = value; break;
        }
    }

    /// <summary>A model from a select's value. Empty means none. Anything else that isn't provider|ID is invalid.</summary>
    private static ModelRef? Model(string? value, out bool valid)
    {
        valid = true;

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var separator = value.IndexOf(ModelSeparator, StringComparison.Ordinal);

        if (separator <= 0 || separator == value.Length - 1)
        {
            valid = false;
            return null;
        }

        return new ModelRef(value[..separator], value[(separator + 1)..]);
    }

    private static decimal? OptionalDecimal(string? value, out bool valid)
    {
        valid = true;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        valid = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number);
        return valid ? number : null;
    }

    private static long? OptionalLong(string? value, out bool valid)
    {
        valid = true;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        valid = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
        return valid ? number : null;
    }

    /// <summary>Without trailing zeros, as an input of type number shows it.</summary>
    private static string? Number(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>An exact name only, because Enum.TryParse also takes a number.</summary>
    private static T? Parse<T>(string? value)
        where T : struct, Enum =>
        value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<T>(value) : null;
}
