using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecnanib.Modules.AiCouncil.Contracts;

namespace Ecnanib.Modules.AiCouncil;

/// <summary>
/// One node of an output contract's schema. The nodes can express only the subset of JSON Schema that every provider
/// enforces (The four providers in docs/architecture.md): an object lists every property in <c>required</c> and has
/// <c>additionalProperties: false</c>, a nullable value is <c>anyOf</c> with <c>null</c>, and there's no
/// <c>minimum</c>, <c>maxLength</c>, <c>pattern</c> or <c>format</c>. The same node writes the schema a provider
/// gets and checks the answer, so the two can't differ. What the subset can't say is in <see cref="ContractRules"/>.
/// </summary>
internal abstract class SchemaNode
{
    /// <summary>What a value must be, after "must be", such as "a number".</summary>
    public abstract string Expectation { get; }

    /// <summary>What a value must be when it may also be null, such as "a number or null".</summary>
    public virtual string NullableExpectation => $"{Expectation} or null";

    public abstract JsonObject ToJson();

    /// <summary>
    /// Adds a violation for each way <paramref name="value"/> doesn't fit, at its path. A violation never quotes
    /// the answer, which a model wrote, so it can be sent back to the model and logged.
    /// </summary>
    public abstract void Validate(JsonElement value, string path, Violations violations);

    protected void Mismatch(string path, Violations violations) => violations.Add(path, $"must be {Expectation}");

    public static string Child(string path, string name) => $"{path}.{name}";

    public static string Item(string path, int index) => string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]");
}

/// <summary>Builds schema nodes.</summary>
internal static partial class Schema
{
    public static SchemaNode Object(params (string Name, SchemaNode Node)[] properties) => new ObjectNode(properties);

    public static SchemaNode Array(SchemaNode items) => new ArrayNode(items);

    public static SchemaNode String() => new StringNode(null);

    /// <summary>A string that must be one of <paramref name="values"/>.</summary>
    public static SchemaNode Enum(IEnumerable<string> values) => new StringNode([.. values]);

    public static SchemaNode Number() => new NumberNode();

    public static SchemaNode Integer() => new IntegerNode();

    public static SchemaNode Boolean() => new BooleanNode();

    /// <summary>A string that must be an ISO 8601 time with its offset. Providers get a plain string, because not all of them take <c>format</c>.</summary>
    public static SchemaNode Time() => new TimeNode();

    public static SchemaNode Nullable(SchemaNode node) => new NullableNode(node);

    private sealed class ObjectNode : SchemaNode
    {
        private readonly (string Name, SchemaNode Node)[] _properties;
        private readonly Dictionary<string, SchemaNode> _byName;

        public ObjectNode((string Name, SchemaNode Node)[] properties)
        {
            _properties = properties;
            _byName = properties.ToDictionary(property => property.Name, property => property.Node, StringComparer.Ordinal);
        }

        public override string Expectation => "an object";

        public override JsonObject ToJson() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(_properties.Select(property =>
                KeyValuePair.Create(property.Name, (JsonNode?)property.Node.ToJson()))),
            ["required"] = new JsonArray([.. _properties.Select(property => (JsonNode)property.Name)]),
            ["additionalProperties"] = false,
        };

        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Mismatch(path, violations);
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unknown = false;

            foreach (var property in value.EnumerateObject())
            {
                if (!_byName.TryGetValue(property.Name, out var node))
                {
                    // Its name is the model's, so the violation lists the contract's instead.
                    unknown = true;
                }
                else if (!seen.Add(property.Name))
                {
                    violations.Add(Child(path, property.Name), "appears more than once");
                }
                else
                {
                    node.Validate(property.Value, Child(path, property.Name), violations);
                }
            }

            foreach (var (name, _) in _properties.Where(property => !seen.Contains(property.Name)))
            {
                violations.Add(Child(path, name), "is missing");
            }

            if (unknown)
            {
                violations.Add(
                    path,
                    $"has properties that aren't in the contract: it takes exactly {string.Join(", ", _properties.Select(property => property.Name))}");
            }
        }
    }

    private sealed class ArrayNode(SchemaNode items) : SchemaNode
    {
        public override string Expectation => "an array";

        public override JsonObject ToJson() => new() { ["type"] = "array", ["items"] = items.ToJson() };

        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                Mismatch(path, violations);
                return;
            }

            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                items.Validate(item, Item(path, index++), violations);
            }
        }
    }

    private sealed class StringNode(string[]? values) : SchemaNode
    {
        public override string Expectation => values switch
        {
            null => "a string",
            [var only] => $"\"{only}\"",
            _ => $"one of {string.Join(", ", values[..^1].Select(Quote))} or {Quote(values[^1])}",
        };

        public override string NullableExpectation => values is null
            ? base.NullableExpectation
            : $"one of {string.Join(", ", values.Select(Quote))} or null";

        public override JsonObject ToJson()
        {
            var json = new JsonObject { ["type"] = "string" };

            if (values is not null)
            {
                json["enum"] = new JsonArray([.. values.Select(value => (JsonNode)value)]);
            }

            return json;
        }

        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind != JsonValueKind.String || (values is not null && !values.Contains(value.GetString(), StringComparer.Ordinal)))
            {
                Mismatch(path, violations);
            }
        }

        private static string Quote(string value) => $"\"{value}\"";
    }

    private sealed class NumberNode : SchemaNode
    {
        public override string Expectation => "a number";

        public override JsonObject ToJson() => new() { ["type"] = "number" };

        /// <remarks>Read as decimal throughout (DATA-R-02), so a number too large for one doesn't fit.</remarks>
        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out _))
            {
                Mismatch(path, violations);
            }
        }
    }

    private sealed class IntegerNode : SchemaNode
    {
        public override string Expectation => "a whole number";

        public override JsonObject ToJson() => new() { ["type"] = "integer" };

        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))
            {
                Mismatch(path, violations);
            }
        }
    }

    private sealed class BooleanNode : SchemaNode
    {
        public override string Expectation => "true or false";

        public override JsonObject ToJson() => new() { ["type"] = "boolean" };

        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                Mismatch(path, violations);
            }
        }
    }

    private sealed partial class TimeNode : SchemaNode
    {
        public override string Expectation => "an ISO 8601 time with its offset, such as \"2026-11-06T09:30:00Z\"";

        public override JsonObject ToJson() => new() { ["type"] = "string" };

        /// <remarks>
        /// The offset is required, because a time without one would be read in the server's own zone.
        /// TryGetDateTimeOffset follows ISO 8601-1's extended profile, as the reader does.
        /// </remarks>
        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out _) || !Offset().IsMatch(value.GetString()!))
            {
                Mismatch(path, violations);
            }
        }

        [GeneratedRegex(@"T.*(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex Offset();
    }

    private sealed class NullableNode(SchemaNode node) : SchemaNode
    {
        public override string Expectation => node.NullableExpectation;

        public override JsonObject ToJson() => new()
        {
            ["anyOf"] = new JsonArray(node.ToJson(), new JsonObject { ["type"] = "null" }),
        };

        public override void Validate(JsonElement value, string path, Violations violations)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            // Only a value of the right kind is checked further, so its own violations aren't lost in "or null".
            var inner = new Violations();
            node.Validate(value, path, inner);

            if (inner.All is [{ } only] && only.Path == path && only.Problem == $"must be {node.Expectation}")
            {
                Mismatch(path, violations);
            }
            else
            {
                violations.AddRange(inner);
            }
        }
    }
}

/// <summary>The violations found so far, in the order they were found.</summary>
internal sealed class Violations
{
    private readonly List<ContractViolation> _all = [];

    public IReadOnlyList<ContractViolation> All => _all;

    public bool Any => _all.Count > 0;

    public void Add(string path, string problem) => _all.Add(new ContractViolation(path, problem));

    public void AddRange(Violations other) => _all.AddRange(other._all);

    /// <summary>The first <see cref="OutputContracts.MaxViolations"/>, which is all a retry sends back.</summary>
    public IReadOnlyList<ContractViolation> Reported() => [.. _all.Take(OutputContracts.MaxViolations)];
}
