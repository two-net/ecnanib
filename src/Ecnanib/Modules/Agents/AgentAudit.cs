using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// An agent's audit records (FR-AUD-01, FR-AUD-02): who changed what, and why. A summary never holds the
/// description or custom instructions, because administrators can search the trail and must not read what
/// users write to their models (FR-ADM-01).
/// </summary>
internal static class AgentAudit
{
    public const string Target = "Agent";

    /// <summary>A change the user made.</summary>
    public static AuditEntry ByUser(AuditAction action, Guid userId, Guid agentId, IReadOnlyList<AuditChange> changes) =>
        AuditEntry.ByUser(action, userId) with { TargetType = Target, TargetId = agentId.ToString(), Changes = changes };

    /// <summary>A change the application made on its own, such as pausing an agent whose key stopped working.</summary>
    public static AuditEntry BySystem(AuditAction action, Guid userId, Guid agentId, IReadOnlyList<AuditChange> changes) => new()
    {
        Action = action,
        ActorKind = AuditActorKind.System,
        UserId = userId,
        TargetType = Target,
        TargetId = agentId.ToString(),
        Changes = changes,
    };

    /// <summary>A change of status, with the reason it's in the new one (FR-AGT-04).</summary>
    public static IReadOnlyList<AuditChange> StatusChange(AgentStatus before, AgentStatus after, AgentStatusReason reason) =>
    [
        new(AgentAuditFields.Status, before.ToString(), after.ToString()),
        new(AgentAuditFields.Reason, null, reason.ToString()),
    ];

    /// <summary>
    /// The settings' fields that differ between <paramref name="before"/> and <paramref name="after"/>, each
    /// with both values. Null on one side lists every field set on the other, as when an agent is created.
    /// </summary>
    public static List<AuditChange> Differences(Described? before, Described? after)
    {
        var old = before?.Fields ?? new Dictionary<string, string?>();
        var current = after?.Fields ?? new Dictionary<string, string?>();

        return
        [
            .. old.Keys.Union(current.Keys)
                .Select(field => new AuditChange(field, old.GetValueOrDefault(field), current.GetValueOrDefault(field)))
                .Where(change => change.Before != change.After),
        ];
    }

    /// <summary>An agent's settings as audit fields, in the order the page shows them.</summary>
    /// <param name="exchangeKey">The exchange key's label, or null when there's none or it's gone.</param>
    public static Described Describe(AgentSettings settings, string quoteAsset, string? exchangeKey)
    {
        var fields = new Dictionary<string, string?>
        {
            [AgentAuditFields.Name] = settings.Name,
            [AgentAuditFields.Market] = settings.Market.ToString(),
            [AgentAuditFields.Environment] = settings.Environment.ToString(),
            [AgentAuditFields.Symbol] = settings.Symbol,
            [AgentAuditFields.Timeframe] = settings.Timeframe.ToString(),
            [AgentAuditFields.ExchangeKey] = exchangeKey,
            [AgentAuditFields.ExecutionMode] = settings.ExecutionMode.ToString(),
            [AgentAuditFields.AutoExecuteRiskReducing] = settings.AutoExecuteRiskReducing.ToString(),
            [AgentAuditFields.CapitalAllocation] = Amount(settings.CapitalAllocation, quoteAsset),
        };

        foreach (var role in Enum.GetValues<AiRole>())
        {
            var assignment = settings.RoleOf(role);
            fields[AgentAuditFields.RoleField(role)] = assignment?.Model.ToString();
            fields[AgentAuditFields.FallbackField(role)] = assignment?.Fallback?.ToString();
        }

        var risk = settings.Risk;

        foreach (var (setting, value) in RiskValues(risk))
        {
            fields[AgentAuditFields.RiskField(setting)] = value;
        }

        fields[AgentAuditFields.DailyBudget] = Amount(settings.Budget.DailyUsd, AiBudget.Currency);
        fields[AgentAuditFields.MonthlyBudget] = Amount(settings.Budget.MonthlyUsd, AiBudget.Currency);
        fields[AgentAuditFields.DailyTokenCap] = settings.Budget.DailyTokens?.ToString(CultureInfo.InvariantCulture);
        fields[AgentAuditFields.MonthlyTokenCap] = settings.Budget.MonthlyTokens?.ToString(CultureInfo.InvariantCulture);
        fields[AgentAuditFields.ExperienceMemory] = settings.Experience.Enabled.ToString();
        fields[AgentAuditFields.ReflectionsInPrompt] = settings.Experience.ReflectionsInPrompt.ToString(CultureInfo.InvariantCulture);

        return new Described(fields.Where(field => field.Value is not null).ToDictionary());
    }

    /// <summary>Each risk setting as invariant text, in the order of <see cref="RiskSetting"/>.</summary>
    public static IEnumerable<(RiskSetting Setting, string? Value)> RiskValues(RiskSettings risk)
    {
        yield return (RiskSetting.MaxRiskPerTradePercent, Number(risk.MaxRiskPerTradePercent));
        yield return (RiskSetting.MaxPositionNotional, Number(risk.MaxPositionNotional));
        yield return (RiskSetting.MaxLeverage, risk.MaxLeverage.ToString(CultureInfo.InvariantCulture));
        yield return (RiskSetting.DailyLossLimitPercent, Number(risk.DailyLossLimitPercent));
        yield return (RiskSetting.WeeklyLossLimitPercent, Number(risk.WeeklyLossLimitPercent));
        yield return (RiskSetting.ConsecutiveLossesBeforePause, risk.ConsecutiveLossesBeforePause.ToString(CultureInfo.InvariantCulture));
        yield return (RiskSetting.MinRewardToRisk, Number(risk.MinRewardToRisk));
        yield return (RiskSetting.MaxNewEntriesPerDay, risk.MaxNewEntriesPerDay.ToString(CultureInfo.InvariantCulture));
        yield return (RiskSetting.MinStopDistanceAtr, Number(risk.MinStopDistanceAtr));
        yield return (RiskSetting.MaxStopDistanceAtr, Number(risk.MaxStopDistanceAtr));
        yield return (RiskSetting.LiquidationBufferMultiple, Number(risk.LiquidationBufferMultiple));
        yield return (RiskSetting.FundingFilterPercent, Number(risk.FundingFilterPercent));
        yield return (RiskSetting.CooldownCyclesAfterLoss, risk.CooldownCyclesAfterLoss.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Without trailing zeros, so a value reads the same however it was typed.</summary>
    private static string? Number(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string? Amount(decimal? value, string asset) => value is null ? null : $"{Number(value)} {asset}";

    /// <summary>An agent's settings as named values.</summary>
    internal sealed record Described(IReadOnlyDictionary<string, string?> Fields);
}
