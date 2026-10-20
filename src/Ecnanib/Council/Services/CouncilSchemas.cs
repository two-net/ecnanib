using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecnanib.Council.Services;

/// <summary>
/// The Appendix C output schemas every role is asked for (FR-AI-07), in the subset all four providers accept (AI
/// module README): a root object, <c>additionalProperties: false</c>, every property required, optional numbers as
/// <c>["number", "null"]</c>, enums on strings, and only type, properties, required, items, enum and description.
/// Bounds (0..1, positive prices, text lengths) are validated in code, not in the schema.
/// </summary>
internal static class CouncilSchemas
{
    public const string Assessment = "assessment";
    public const string Confidence = "confidence";
    public const string ProposedAction = "proposed_action";
    public const string EntryType = "entry_type";
    public const string EntryPrice = "entry_price";
    public const string StopPrice = "stop_price";
    public const string Targets = "targets";
    public const string Flags = "flags";
    public const string RiskPercent = "risk_percent";
    public const string Rationale = "rationale";
    public const string TakePartialFraction = "take_partial_fraction";

    public static readonly IReadOnlyList<string> Actions = ["open_long", "open_short", "hold", "modify", "close", "no_trade"];

    public static readonly IReadOnlyList<string> EntryTypes = ["limit", "market", "none"];

    /// <summary>What the three non-final roles return (Appendix C "role output").</summary>
    public static JsonElement RoleOutput { get; } = Build(final: false);

    /// <summary>What the Risk Manager returns (Appendix C "final decision", FR-AI-10).</summary>
    public static JsonElement FinalDecision { get; } = Build(final: true);

    public static JsonElement For(bool final) => final ? FinalDecision : RoleOutput;

    private static JsonElement Build(bool final)
    {
        var properties = new JsonObject
        {
            [Assessment] = Typed("string", "Your assessment in plain prose, within the length limit you were given."),
            [Confidence] = Typed("number", "Confidence in the assessment from 0 (none) to 1 (certain)."),
            [ProposedAction] = Enum(Actions, "The action you propose: open_long, open_short, hold, modify, close or no_trade."),
            [EntryType] = Enum(EntryTypes, "limit or market when proposing an entry, otherwise none."),
            [EntryPrice] = Nullable("number", "Limit entry price in the quote asset; null for a market entry or when no entry is proposed."),
            [StopPrice] = Nullable("number", "Stop price; required for any entry and for a modify that moves the stop, otherwise null."),
            [Targets] = Array("number", "Target prices in the quote asset, nearest first; empty when none."),
            [Flags] = Array("string", "Short snake_case tags that summarise risks or conditions; empty when none."),
        };
        if (final)
        {
            properties[RiskPercent] = Nullable("number", "Percent of equity to risk on a new entry, at most the agent's limit; null when not opening.");
            properties[Rationale] = Typed("string", "Why you decided this, in plain prose, within the length limit you were given.");
            properties[TakePartialFraction] = Nullable("number", "For modify: the fraction of the position (0 to 1) to close now; null otherwise.");
        }

        var required = new JsonArray();
        foreach (var property in properties)
            required.Add(property.Key);

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["description"] = final ? "The Risk Manager's final decision." : "One council role's output.",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject Typed(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Nullable(string type, string description) => new() { ["type"] = new JsonArray(type, "null"), ["description"] = description };

    private static JsonObject Enum(IReadOnlyList<string> values, string description)
    {
        var options = new JsonArray();
        foreach (var value in values)
            options.Add(value);
        return new JsonObject { ["type"] = "string", ["enum"] = options, ["description"] = description };
    }

    private static JsonObject Array(string itemType, string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = itemType }, ["description"] = description };
}
