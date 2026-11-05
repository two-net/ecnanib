using System.Text;
using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Agents;

/// <summary>What a user's settings must be before anything is stored or Binance is asked about the pair.</summary>
internal static partial class AgentInput
{
    /// <summary>The settings, cleaned, or what's wrong with them.</summary>
    /// <param name="Settings">Null when <paramref name="Fields"/> isn't empty.</param>
    internal sealed record Checked(AgentSettings? Settings, IReadOnlyList<AgentField> Fields, IReadOnlyList<RiskSetting> RiskSettings);

    /// <summary>
    /// Cleans the text, as <see cref="Text"/> says, and puts the pair in upper case, then checks every setting's
    /// own range. Whether Binance lists the pair, and whether the exchange key is the user's, are checked by
    /// the caller.
    /// </summary>
    public static Checked Check(AgentSettings settings)
    {
        var fields = new List<AgentField>();

        void Require(bool valid, AgentField field)
        {
            if (!valid && !fields.Contains(field))
            {
                fields.Add(field);
            }
        }

        var name = Text(settings.Name, AgentLimits.NameMaxLength, multiline: false);
        Require(name is not null, AgentField.Name);

        var description = Optional(settings.Description, AgentLimits.DescriptionMaxLength, multiline: true, out var descriptionValid);
        Require(descriptionValid, AgentField.Description);

        var instructions = Optional(settings.CustomInstructions, AgentLimits.CustomInstructionsMaxLength, multiline: true, out var instructionsValid);
        Require(instructionsValid, AgentField.CustomInstructions);

        var symbol = (settings.Symbol ?? "").Trim().ToUpperInvariant();
        Require(SymbolShape().IsMatch(symbol), AgentField.Symbol);

        // A value outside an enum can only come from a crafted request.
        Require(Enum.IsDefined(settings.Market), AgentField.Market);
        Require(Enum.IsDefined(settings.Environment), AgentField.Environment);
        Require(Enum.IsDefined(settings.Timeframe), AgentField.Timeframe);
        Require(Enum.IsDefined(settings.ExecutionMode), AgentField.ExecutionMode);

        Require(settings.Roles is not null && RolesValid(settings.Roles), AgentField.Roles);
        Require(settings.CapitalAllocation is null or (> 0m and <= AgentLimits.MaxAmount), AgentField.CapitalAllocation);

        var risk = settings.Risk ?? RiskSettings.Default;
        var riskProblems = risk.Problems().ToList();

        if (risk.MaxPositionNotional > AgentLimits.MaxAmount && !riskProblems.Contains(RiskSetting.MaxPositionNotional))
        {
            riskProblems.Add(RiskSetting.MaxPositionNotional);
        }

        Require(riskProblems.Count == 0, AgentField.Risk);

        var budget = settings.Budget ?? AiBudget.None;
        Require(BudgetValid(budget), AgentField.Budget);

        var experience = settings.Experience ?? ExperienceMemory.Default;
        Require(experience.ReflectionsInPrompt is >= 0 and <= AgentLimits.MaxReflectionsInPrompt, AgentField.ReflectionsInPrompt);

        if (fields.Count > 0)
        {
            return new Checked(null, fields, riskProblems);
        }

        return new Checked(
            settings with
            {
                Name = name!,
                Description = description,
                Symbol = symbol,
                Roles = [.. settings.Roles!.OrderBy(role => role.Role)],
                Risk = risk,
                Budget = budget,
                Experience = experience,
                CustomInstructions = instructions,
            },
            [],
            []);
    }

    /// <summary>
    /// What PostgreSQL can store and a page can show: no control characters, which include a NUL, and no half
    /// of a surrogate pair, trimmed. Multi-line text keeps its line breaks, as <c>\n</c>. Null when nothing is
    /// left or it's longer than <paramref name="maxLength"/>.
    /// </summary>
    public static string? Text(string? text, int maxLength, bool multiline)
    {
        if (text is null)
        {
            return null;
        }

        var kept = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    kept.Append(character).Append(text[++index]);
                }
            }
            else if (multiline && character == '\n')
            {
                kept.Append(character);
            }
            else if (!char.IsLowSurrogate(character) && !char.IsControl(character))
            {
                kept.Append(character);
            }
        }

        var cleaned = kept.ToString().Trim();

        return cleaned.Length == 0 || cleaned.Length > maxLength ? null : cleaned;
    }

    /// <summary>Like <see cref="Text"/>, except that nothing is fine, and gives null.</summary>
    private static string? Optional(string? text, int maxLength, bool multiline, out bool valid)
    {
        var cleaned = Text(text, int.MaxValue, multiline);
        valid = cleaned is null || cleaned.Length <= maxLength;

        return valid ? cleaned : null;
    }

    private static bool RolesValid(IReadOnlyList<RoleAssignment> roles) =>
        roles.All(role => role is not null && Enum.IsDefined(role.Role) && ModelValid(role.Model)
            && (role.Fallback is null || (ModelValid(role.Fallback) && role.Fallback != role.Model)))
        && roles.Select(role => role.Role).Distinct().Count() == roles.Count;

    private static bool ModelValid(ModelRef? model) =>
        model is { Provider: { Length: > 0 and <= Agent.ProviderMaxLength }, ModelId: { Length: > 0 and <= Agent.ModelMaxLength } }
        && model.Provider.Trim() == model.Provider
        && model.ModelId.Trim() == model.ModelId
        && !model.Provider.Any(char.IsControl)
        && !model.ModelId.Any(char.IsControl);

    private static bool BudgetValid(AiBudget budget) =>
        budget.DailyUsd is null or (> 0m and <= AgentLimits.MaxAmount)
        && budget.MonthlyUsd is null or (> 0m and <= AgentLimits.MaxAmount)
        && budget.DailyTokens is null or (> 0L and <= AgentLimits.MaxTokens)
        && budget.MonthlyTokens is null or (> 0L and <= AgentLimits.MaxTokens)
        && !(budget.DailyUsd > budget.MonthlyUsd)
        && !(budget.DailyTokens > budget.MonthlyTokens);

    /// <summary>Binance's symbols are upper-case letters and digits.</summary>
    [GeneratedRegex("^[A-Z0-9]{2,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolShape();
}
