using System.Globalization;
using System.Text.Json;
using Ecnanib.Council.Abstractions;
using Ecnanib.Council.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Council.Services;

/// <summary>
/// Validates a role's answer against Appendix C (FR-AI-07): the document shape, the enums, confidence within 0..1,
/// positive prices, the fields an action needs (an entry needs a stop, a limit entry a price, a modify something
/// to change) and the configured text bounds, which are applied by cutting rather than rejecting. A rejection reason
/// is worded for the model: it is appended to the one retry.
/// </summary>
internal static class RoleOutputParser
{
    public const int MaxTargets = 5;

    public const int MaxFlags = 10;

    public const int MaxFlagLength = 60;

    public static RoleOutputDocument? Parse(string text, bool final, CouncilOptions options, out string? rejection)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            rejection = $"the answer is not valid JSON ({ex.Message})";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                rejection = "the answer must be one JSON object";
                return null;
            }

            var problems = new List<string>();
            var assessment = ReadText(root, CouncilSchemas.Assessment, options.MaxAssessmentLength, problems);
            var confidence = ReadNumber(root, CouncilSchemas.Confidence, nullable: false, problems);
            if (confidence is < 0 or > 1)
                problems.Add($"'{CouncilSchemas.Confidence}' must be between 0 and 1");
            var action = ReadAction(root, problems);
            var entryType = ReadEntryType(root, problems);
            var entryPrice = ReadPrice(root, CouncilSchemas.EntryPrice, problems);
            var stopPrice = ReadPrice(root, CouncilSchemas.StopPrice, problems);
            var targets = ReadTargets(root, problems);
            var flags = ReadFlags(root, problems);
            decimal? riskPercent = null;
            decimal? partial = null;
            string rationale = "";
            if (final)
            {
                riskPercent = ReadNumber(root, CouncilSchemas.RiskPercent, nullable: true, problems);
                if (riskPercent is <= 0 or > 100)
                    problems.Add($"'{CouncilSchemas.RiskPercent}' must be greater than 0 and at most 100");
                rationale = ReadText(root, CouncilSchemas.Rationale, options.MaxRationaleLength, problems);
                partial = ReadNumber(root, CouncilSchemas.TakePartialFraction, nullable: true, problems);
                if (partial is <= 0 or > 1)
                    problems.Add($"'{CouncilSchemas.TakePartialFraction}' must be greater than 0 and at most 1");
            }

            if (problems.Count == 0 && action is { } proposed)
            {
                if (entryType == OrderType.Market)
                    entryPrice = null;
                switch (proposed)
                {
                    case DecisionAction.OpenLong or DecisionAction.OpenShort:
                        if (stopPrice is null)
                            problems.Add($"an entry needs a '{CouncilSchemas.StopPrice}'");
                        if (entryType is null)
                            problems.Add($"an entry needs '{CouncilSchemas.EntryType}' limit or market");
                        else if (entryType == OrderType.Limit && entryPrice is null)
                            problems.Add($"a limit entry needs an '{CouncilSchemas.EntryPrice}'");
                        break;
                    case DecisionAction.Modify:
                        if (stopPrice is null && targets.Count == 0 && partial is null)
                            problems.Add($"a modify needs a '{CouncilSchemas.StopPrice}', '{CouncilSchemas.Targets}'{(final ? $" or '{CouncilSchemas.TakePartialFraction}'" : "")}");
                        break;
                    default:
                        entryType = null;
                        break;
                }
            }

            if (problems.Count > 0)
            {
                rejection = string.Join("; ", problems);
                return null;
            }

            rejection = null;
            return final
                ? new FinalDecisionDocument(assessment, confidence!.Value, action!.Value, entryType, entryPrice, stopPrice, targets, flags, riskPercent, rationale, partial)
                : new RoleOutputDocument(assessment, confidence!.Value, action!.Value, entryType, entryPrice, stopPrice, targets, flags);
        }
    }

    private static string ReadText(JsonElement root, string name, int maxLength, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            problems.Add($"'{name}' must be a string");
            return "";
        }

        var text = value.GetString()!.Trim();
        if (text.Length == 0)
        {
            problems.Add($"'{name}' must not be empty");
            return "";
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static decimal? ReadNumber(JsonElement root, string name, bool nullable, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var value) || (!nullable && value.ValueKind != JsonValueKind.Number))
        {
            problems.Add($"'{name}' must be a number");
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;
        problems.Add($"'{name}' must be a number or null");
        return null;
    }

    private static decimal? ReadPrice(JsonElement root, string name, List<string> problems)
    {
        var price = ReadNumber(root, name, nullable: true, problems);
        if (price is <= 0)
            problems.Add($"'{name}' must be greater than 0");
        return price;
    }

    private static DecisionAction? ReadAction(JsonElement root, List<string> problems)
    {
        if (root.TryGetProperty(CouncilSchemas.ProposedAction, out var value) && value.ValueKind == JsonValueKind.String)
        {
            switch (value.GetString())
            {
                case "open_long": return DecisionAction.OpenLong;
                case "open_short": return DecisionAction.OpenShort;
                case "hold": return DecisionAction.Hold;
                case "modify": return DecisionAction.Modify;
                case "close": return DecisionAction.Close;
                case "no_trade": return DecisionAction.NoTrade;
            }
        }

        problems.Add($"'{CouncilSchemas.ProposedAction}' must be one of {string.Join(", ", CouncilSchemas.Actions)}");
        return null;
    }

    private static OrderType? ReadEntryType(JsonElement root, List<string> problems)
    {
        if (root.TryGetProperty(CouncilSchemas.EntryType, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
        {
            switch (value.ValueKind == JsonValueKind.Null ? "none" : value.GetString())
            {
                case "limit": return OrderType.Limit;
                case "market": return OrderType.Market;
                case "none": return null;
            }
        }

        problems.Add($"'{CouncilSchemas.EntryType}' must be one of {string.Join(", ", CouncilSchemas.EntryTypes)}");
        return null;
    }

    private static IReadOnlyList<decimal> ReadTargets(JsonElement root, List<string> problems)
    {
        if (!root.TryGetProperty(CouncilSchemas.Targets, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"'{CouncilSchemas.Targets}' must be an array of numbers");
            return [];
        }

        var targets = new List<decimal>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDecimal(out var price) || price <= 0)
            {
                problems.Add($"every '{CouncilSchemas.Targets}' entry must be a number greater than 0");
                return [];
            }

            if (targets.Count < MaxTargets)
                targets.Add(price);
        }

        return targets;
    }

    private static IReadOnlyList<string> ReadFlags(JsonElement root, List<string> problems)
    {
        if (!root.TryGetProperty(CouncilSchemas.Flags, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"'{CouncilSchemas.Flags}' must be an array of strings");
            return [];
        }

        var flags = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                problems.Add($"every '{CouncilSchemas.Flags}' entry must be a string");
                return [];
            }

            var flag = item.GetString()!.Trim();
            if (flag.Length == 0 || flags.Contains(flag, StringComparer.OrdinalIgnoreCase))
                continue;
            flags.Add(flag.Length <= MaxFlagLength ? flag : flag[..MaxFlagLength]);
            if (flags.Count == MaxFlags)
                break;
        }

        return flags;
    }

    public static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
