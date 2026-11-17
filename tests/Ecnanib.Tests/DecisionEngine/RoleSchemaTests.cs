using System.Text.Json;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Each role returns a structured result that is validated against a fixed schema, plus a short
/// rationale (FR-TEAM-6). These hold the four schemas: to the structured result that the
/// specification gives each role (SRS 4.5), and to what all four providers take, since a schema
/// is sent to the provider as it is. See "The schemas" in docs/team.md.
/// </summary>
public class RoleSchemaTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    public static TheoryData<Role> Roles => [.. Enum.GetValues<Role>()];

    private IRoleResults Results => application.Services.GetRequiredService<IRoleResults>();

    [Fact]
    public void Each_role_answers_the_structured_result_of_the_specification_with_a_rationale()
    {
        // Bias (bullish, bearish, or neutral), key price levels, volatility read.
        Assert.Equal(["bias", "keyLevels", "volatility", "rationale"], FieldsOf(Role.MarketAnalyst));
        Assert.Equal(["bullish", "bearish", "neutral"], ValuesOf(Role.MarketAnalyst, "bias"));

        // Bias, notable events, confidence, and whether live data was available.
        Assert.Equal(["bias", "notableEvents", "confidence", "liveDataAvailable", "rationale"], FieldsOf(Role.SentimentAnalyst));

        // Action, entry type and price, stop-loss, take-profit, risk fraction, rationale. The
        // actions are those of the two markets together (FR-CYC-4), and an entry is a market
        // or a limit order (FR-BIN-S2).
        Assert.Equal(
            ["action", "entryType", "entryPrice", "stopLoss", "takeProfit", "riskFraction", "rationale"],
            FieldsOf(Role.Strategist));
        Assert.Equal(
            ["open_long", "open_short", "hold", "close", "reverse", "adjust_protection"],
            ValuesOf(Role.Strategist, "action"));
        Assert.Equal(["market", "limit", null], ValuesOf(Role.Strategist, "entryType"));

        // Approve, reduce, or veto, with reasons. The reasons are its rationale.
        Assert.Equal(["verdict", "riskFraction", "rationale"], FieldsOf(Role.RiskManager));
        Assert.Equal(["approve", "reduce", "veto"], ValuesOf(Role.RiskManager, "verdict"));
    }

    [Fact]
    public void No_schema_has_a_place_for_a_quantity_a_leverage_or_anything_that_is_carried_out()
    {
        // Models do not choose quantity or leverage (FR-RSK-5), and cannot place orders or call
        // internal functions (NFR-SEC-6): a result is the fields above, which are read as data.
        var fields = Enum.GetValues<Role>().SelectMany(FieldsOf).Distinct().ToList();

        Assert.DoesNotContain(fields, field =>
            field.Contains("quantity", StringComparison.OrdinalIgnoreCase)
            || field.Contains("leverage", StringComparison.OrdinalIgnoreCase)
            || field.Contains("size", StringComparison.OrdinalIgnoreCase)
            || field.Contains("order", StringComparison.OrdinalIgnoreCase)
            || field.Contains("call", StringComparison.OrdinalIgnoreCase)
            || field.Contains("tool", StringComparison.OrdinalIgnoreCase));

        // The review of the Risk Manager has no direction, no entry, and no stop-loss, so it
        // can change none of them (FR-TEAM-7).
        Assert.Empty(FieldsOf(Role.RiskManager).Intersect(["action", "entryType", "entryPrice", "stopLoss", "takeProfit"]));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void A_schema_is_fixed_and_is_one_line(Role role)
    {
        var schema = Results.SchemaOf(role);

        // The same document whenever it is asked for, and the same in every checkout.
        Assert.Equal(schema, Results.SchemaOf(role));
        Assert.DoesNotContain('\n', schema);
        Assert.DoesNotContain('\r', schema);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void A_schema_stays_inside_what_all_four_providers_take(Role role)
    {
        using var schema = JsonDocument.Parse(Results.SchemaOf(role));

        // The root is an object.
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());

        var violations = new List<string>();
        Walk(schema.RootElement, "$", violations);

        Assert.True(
            violations.Count == 0,
            "A schema is sent to the provider as it is, so it stays inside what all four take. See \"Result schemas\" in " +
            "docs/ai-providers.md:" + Environment.NewLine + string.Join(Environment.NewLine, violations.Select(violation => $"  {violation}")));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task The_instructions_of_a_role_name_every_field_of_its_schema_and_every_value_that_it_lists(Role role)
    {
        // A role is told what to answer in two places: the schema, which its provider is sent
        // with the call, and its instructions, which say what each field means. They name the
        // same fields.
        var inUse = await application.Services.GetRequiredService<IPromptTemplates>()
            .InUseAsync(Market.Futures, Timeframe.OneHour, TestContext.Current.CancellationToken);
        var result = Assert.Single(
            inUse.Roles[role].Text.Split('\n'), paragraph => paragraph.StartsWith("Result: ", StringComparison.Ordinal));

        using var schema = JsonDocument.Parse(Results.SchemaOf(role));
        var named = new List<string>();
        Collect(schema.RootElement, named);

        Assert.NotEmpty(named);
        Assert.All(named, name => Assert.Contains(name, result, StringComparison.Ordinal));

        // A field is written as the schema writes it, between quotes, and none is named that the schema lacks.
        var quoted = System.Text.RegularExpressions.Regex.Matches(result, "\"([A-Za-z]+)\"").Select(match => match.Groups[1].Value).Distinct();

        Assert.Equal(FieldsWithin(schema.RootElement).Order(StringComparer.Ordinal), quoted.Order(StringComparer.Ordinal));

        static void Collect(JsonElement schema, List<string> named)
        {
            if (schema.TryGetProperty("enum", out var values))
            {
                named.AddRange(values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!));
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    named.Add($"\"{property.Name}\"");
                    Collect(property.Value, named);
                }
            }

            if (schema.TryGetProperty("items", out var items))
            {
                Collect(items, named);
            }
        }

        static IEnumerable<string> FieldsWithin(JsonElement schema)
        {
            var fields = schema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().SelectMany(property => FieldsWithin(property.Value).Prepend(property.Name))
                : [];

            return schema.TryGetProperty("items", out var items) ? fields.Concat(FieldsWithin(items)) : fields;
        }
    }

    /// <summary>The fields of what a role answers, in the order of its schema.</summary>
    private List<string> FieldsOf(Role role)
    {
        using var schema = JsonDocument.Parse(Results.SchemaOf(role));

        return [.. schema.RootElement.GetProperty("properties").EnumerateObject().Select(field => field.Name)];
    }

    /// <summary>The values that the schema of a role lists for a field.</summary>
    private List<string?> ValuesOf(Role role, string field)
    {
        using var schema = JsonDocument.Parse(Results.SchemaOf(role));

        return [.. schema.RootElement.GetProperty("properties").GetProperty(field).GetProperty("enum").EnumerateArray().Select(value => value.GetString())];
    }

    private static void Walk(JsonElement schema, string path, List<string> violations)
    {
        string[] keywords = ["type", "enum", "properties", "required", "additionalProperties", "items"];
        string[] types = ["string", "number", "integer", "boolean", "array", "object", "null"];

        // No minimum, maximum, multipleOf, minLength, or maxLength, and no reference.
        violations.AddRange(
            from keyword in schema.EnumerateObject()
            where !keywords.Contains(keyword.Name)
            select $"{path}: says \"{keyword.Name}\".");

        var type = schema.GetProperty("type");
        var named = type.ValueKind == JsonValueKind.Array
            ? [.. type.EnumerateArray().Select(one => one.GetString()!)]
            : new List<string> { type.GetString()! };

        violations.AddRange(named.Where(one => !types.Contains(one)).Select(one => $"{path}: is of the type \"{one}\"."));

        if (named.Contains("object"))
        {
            // Every object lists all of its properties in required, and takes no others.
            var properties = schema.GetProperty("properties").EnumerateObject().ToList();
            var required = schema.TryGetProperty("required", out var listed)
                ? listed.EnumerateArray().Select(name => name.GetString()!).ToList()
                : [];

            if (!properties.Select(property => property.Name).SequenceEqual(required))
            {
                violations.Add($"{path}: does not list all of its properties in required.");
            }

            if (!schema.TryGetProperty("additionalProperties", out var others) || others.ValueKind != JsonValueKind.False)
            {
                violations.Add($"{path}: does not set additionalProperties to false.");
            }

            foreach (var property in properties)
            {
                Walk(property.Value, $"{path}.{property.Name}", violations);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            Walk(items, $"{path}[]", violations);
        }
    }
}
