using System.Text.Json;
using Ecnanib.Council.Services;

namespace Ecnanib.Tests.Council.Services;

/// <summary>The Appendix C schemas stay within the subset every provider accepts (AI module README, FR-AI-07).</summary>
public class CouncilSchemasTests
{
    private static readonly string[] AllowedKeywords = ["type", "properties", "required", "items", "enum", "description", "additionalProperties"];

    [Fact]
    public void TheRoleOutputSchemaUsesOnlyTheProviderSafeSubset()
    {
        var root = CouncilSchemas.RoleOutput;

        Walk(root);
        Assert.Equal(["assessment", "confidence", "proposed_action", "entry_type", "entry_price", "stop_price", "targets", "flags"], root.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public void TheFinalDecisionSchemaAddsTheRiskManagerFields()
    {
        var root = CouncilSchemas.FinalDecision;

        Walk(root);
        Assert.Equal(["assessment", "confidence", "proposed_action", "entry_type", "entry_price", "stop_price", "targets", "flags", "risk_percent", "rationale", "take_partial_fraction"], root.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(["open_long", "open_short", "hold", "modify", "close", "no_trade"], root.GetProperty("properties").GetProperty("proposed_action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["limit", "market", "none"], root.GetProperty("properties").GetProperty("entry_type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["number", "null"], root.GetProperty("properties").GetProperty("risk_percent").GetProperty("type").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(CouncilSchemas.FinalDecision.GetRawText(), CouncilSchemas.For(final: true).GetRawText());
        Assert.Equal(CouncilSchemas.RoleOutput.GetRawText(), CouncilSchemas.For(final: false).GetRawText());
    }

    [Fact]
    public void TheSchemasAreStandaloneObjectsTheRequestAccepts()
    {
        var request = new Ecnanib.Ai.Abstractions.AiRequest("m", "s", "u", CouncilSchemas.FinalDecision);

        Assert.Equal(JsonValueKind.Object, request.OutputSchema!.Value.ValueKind);
    }

    private static void Walk(JsonElement schema)
    {
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        foreach (var keyword in schema.EnumerateObject())
            Assert.Contains(keyword.Name, AllowedKeywords);

        var type = schema.GetProperty("type");
        if (type.ValueKind == JsonValueKind.Array)
        {
            var types = type.EnumerateArray().Select(t => t.GetString()).ToList();
            Assert.Equal(2, types.Count);
            Assert.Equal("null", types[1]);
            return;
        }

        switch (type.GetString())
        {
            case "object":
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                var names = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
                Assert.Equal(names, schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
                foreach (var property in schema.GetProperty("properties").EnumerateObject())
                    Walk(property.Value);
                break;
            case "array":
                Walk(schema.GetProperty("items"));
                break;
            case "string" or "number":
                break;
            default:
                Assert.Fail($"unexpected type {type.GetString()}");
                break;
        }
    }
}
