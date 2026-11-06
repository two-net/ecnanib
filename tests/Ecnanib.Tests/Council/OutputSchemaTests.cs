using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Prompts;

namespace Ecnanib.Tests.Council;

/// <summary>
/// FR-PRM-08: each contract's schema stays in the subset every provider enforces (The four providers in
/// docs/architecture.md), because OpenAI's strict mode fails a request outside it, and Anthropic refuses some
/// keywords. PromptContentTests checks that each schema's names are its template's.
/// </summary>
public sealed partial class OutputSchemaTests
{
    private static readonly string[] Keywords = ["type", "properties", "required", "additionalProperties", "items", "enum", "anyOf"];

    private static readonly string[] Types = ["object", "array", "string", "number", "integer", "boolean", "null"];

    public static TheoryData<AiRole, Market> Contracts
    {
        get
        {
            var contracts = new TheoryData<AiRole, Market>();

            foreach (var role in Enum.GetValues<AiRole>())
            {
                foreach (var market in Enum.GetValues<Market>())
                {
                    contracts.Add(role, market);
                }
            }

            return contracts;
        }
    }

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Each_schema_stays_in_the_subset_every_provider_enforces(AiRole role, Market market)
    {
        var schema = OutputContracts.SchemaFor(role, market);

        Assert.Matches("^[a-z_]{1,64}$", schema.Name);
        Assert.Equal("object", schema.Schema.GetProperty("type").GetString());
        Walk(schema.Schema, "$");
    }

    [Theory]
    [InlineData(AiRole.TechnicalAnalyst, "technical_assessment")]
    [InlineData(AiRole.SentimentAnalyst, "sentiment_assessment")]
    [InlineData(AiRole.Strategist, "trade_proposal")]
    [InlineData(AiRole.RiskCritic, "risk_verdict")]
    [InlineData(AiRole.Reviewer, "reflection")]
    public void Each_schema_is_named_for_its_contract(AiRole role, string name) =>
        Assert.Equal(name, OutputContracts.SchemaFor(role, Market.Spot).Name);

    /// <summary>FR-CYC-03: a provider that enforces the schema enforces each market's actions too.</summary>
    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public void The_strategists_actions_are_exactly_the_markets(Market market)
    {
        IntentAction[] spot = [IntentAction.Buy, IntentAction.Sell, IntentAction.AdjustStop, IntentAction.AdjustTarget, IntentAction.CancelPending];
        IntentAction[] futures =
            [IntentAction.OpenLong, IntentAction.OpenShort, IntentAction.Close, IntentAction.AdjustStop, IntentAction.AdjustTarget, IntentAction.CancelPending];
        var expected = (market == Market.Spot ? spot : futures).Select(action => PromptHarness.Screaming(action.ToString())).Append("HOLD").ToList();

        var actions = OutputContracts.SchemaFor(AiRole.Strategist, market).Schema
            .GetProperty("properties").GetProperty("action").GetProperty("enum")
            .EnumerateArray().Select(action => action.GetString()!).ToList();

        Assert.Equal(expected, actions);
        Assert.Equal(expected, TradeActions.For(market).Select(TradeActions.Name).ToList());
    }

    [Theory]
    [InlineData(AiRole.TechnicalAnalyst)]
    [InlineData(AiRole.SentimentAnalyst)]
    [InlineData(AiRole.RiskCritic)]
    [InlineData(AiRole.Reviewer)]
    public void Only_the_strategists_schema_differs_by_market(AiRole role) =>
        Assert.Equal(
            OutputContracts.SchemaFor(role, Market.Spot).Schema.GetRawText(),
            OutputContracts.SchemaFor(role, Market.Futures).Schema.GetRawText());

    [Fact]
    public void Actions_are_written_as_the_srs_writes_them()
    {
        Assert.Equal(
            ["HOLD", "BUY", "SELL", "OPEN_LONG", "OPEN_SHORT", "CLOSE", "ADJUST_STOP", "ADJUST_TARGET", "CANCEL_PENDING"],
            Enum.GetValues<TradeAction>().Select(TradeActions.Name).ToArray());
        Assert.True(TradeActions.IsEntry(TradeAction.OpenShort));
        Assert.False(TradeActions.IsEntry(TradeAction.Close));
        Assert.True(TradeActions.IsExit(TradeAction.Sell));
        Assert.False(TradeActions.IsExit(TradeAction.Hold));
    }

    /// <summary>Every property name in <paramref name="schema"/>, at any depth.</summary>
    public static IEnumerable<string> PropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name == "properties")
            {
                foreach (var child in property.Value.EnumerateObject())
                {
                    yield return child.Name;

                    foreach (var name in PropertyNames(child.Value))
                    {
                        yield return name;
                    }
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in property.Value.EnumerateArray().SelectMany(PropertyNames))
                {
                    yield return name;
                }
            }
            else
            {
                foreach (var name in PropertyNames(property.Value))
                {
                    yield return name;
                }
            }
        }
    }

    /// <summary>
    /// Each node uses only the subset's keywords. An object lists every property in <c>required</c> and has
    /// <c>additionalProperties: false</c>, a nullable value is <c>anyOf</c> a type and <c>null</c>, and an enum is of
    /// strings.
    /// </summary>
    private static void Walk(JsonElement node, string path)
    {
        Assert.True(node.ValueKind == JsonValueKind.Object, $"{path} isn't a schema.");

        var keywords = node.EnumerateObject().Select(property => property.Name).ToList();
        Assert.All(keywords, keyword => Assert.True(Keywords.Contains(keyword), $"{path} uses {keyword}, which not every provider enforces."));

        if (node.TryGetProperty("anyOf", out var anyOf))
        {
            Assert.Equal(["anyOf"], keywords);
            var options = anyOf.EnumerateArray().ToList();
            Assert.Equal(2, options.Count);
            Assert.Equal("""{"type":"null"}""", options[1].GetRawText());
            Walk(options[0], path);
            return;
        }

        var type = node.GetProperty("type").GetString();
        Assert.Contains(type, Types);

        switch (type)
        {
            case "object":
                var properties = node.GetProperty("properties").EnumerateObject().ToList();
                Assert.NotEmpty(properties);
                Assert.Equal(
                    properties.Select(property => property.Name),
                    node.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
                Assert.Equal(JsonValueKind.False, node.GetProperty("additionalProperties").ValueKind);
                Assert.All(properties, property =>
                {
                    Assert.Matches(SnakeCase(), property.Name);
                    Walk(property.Value, $"{path}.{property.Name}");
                });
                break;

            case "array":
                Assert.Equal(["type", "items"], keywords);
                Walk(node.GetProperty("items"), $"{path}[]");
                break;

            case "string" when node.TryGetProperty("enum", out var values):
                Assert.Equal(["type", "enum"], keywords);
                Assert.NotEmpty(values.EnumerateArray());
                Assert.All(values.EnumerateArray(), value => Assert.Equal(JsonValueKind.String, value.ValueKind));
                break;

            default:
                Assert.Equal(["type"], keywords);
                break;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCase();
}
