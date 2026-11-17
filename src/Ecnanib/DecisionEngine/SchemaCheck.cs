using System.Text.Json;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Checks a value against a JSON Schema: the very document that the provider was sent with the
/// call (FR-TEAM-6). So what a role is asked for and what its answer is held to cannot differ.
/// See "Validating a result" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// It knows the part of JSON Schema that a result schema may use, and no more: <c>type</c>, also
/// as a list of types, <c>enum</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c> that is <c>false</c>, and <c>items</c>. See "Result schemas" in
/// docs/ai-providers.md. A schema that says anything else is an error of the service, and throws:
/// nothing that a schema says goes unchecked.
/// </para>
/// <para>
/// An object has the properties of its schema and no others, each once. A parser keeps the last
/// of two properties of one name, and what places the answer in a prompt would place both.
/// </para>
/// </remarks>
internal static class SchemaCheck
{
    private static readonly HashSet<string> Keywords =
        new(StringComparer.Ordinal) { "type", "enum", "properties", "required", "additionalProperties", "items" };

    /// <summary>What a value breaks of a schema, or null when it is inside it.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="value">The value.</param>
    public static RoleRejection? Violation(JsonElement schema, JsonElement value) =>
        Violation(schema, value, path: string.Empty);

    private static RoleRejection? Violation(JsonElement schema, JsonElement value, string path)
    {
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!Keywords.Contains(keyword.Name))
            {
                throw new InvalidOperationException(
                    $"A result schema says \"{keyword.Name}\", which nothing checks. A schema uses type, enum, properties, " +
                    "required, additionalProperties, and items only. See docs/team.md.");
            }
        }

        if (schema.TryGetProperty("type", out var type) && !IsOf(type, value))
        {
            return Rejected(RejectionRule.WrongType, path);
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(one => Same(one, value)))
        {
            return Rejected(RejectionRule.NotAllowed, path);
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => OfObject(schema, value, path),
            JsonValueKind.Array => OfArray(schema, value, path),
            _ => null,
        };
    }

    private static RoleRejection? OfObject(JsonElement schema, JsonElement value, string path)
    {
        if (!schema.TryGetProperty("properties", out var properties)
            || !schema.TryGetProperty("additionalProperties", out var others)
            || others.ValueKind != JsonValueKind.False)
        {
            throw new InvalidOperationException(
                "A result schema has an object that does not list its properties, or that takes others. " +
                "Every object sets additionalProperties to false. See docs/team.md.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in value.EnumerateObject())
        {
            // The name of a field that the schema does not define is the model's, and is not repeated.
            if (!properties.TryGetProperty(property.Name, out var of))
            {
                return Rejected(RejectionRule.UnknownField, path);
            }

            var field = path.Length == 0 ? property.Name : $"{path}.{property.Name}";

            if (!seen.Add(property.Name))
            {
                return Rejected(RejectionRule.DuplicateField, field);
            }

            if (Violation(of, property.Value, field) is { } inner)
            {
                return inner;
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(name => name.GetString()!))
            {
                if (!seen.Contains(name))
                {
                    return Rejected(RejectionRule.MissingField, path.Length == 0 ? name : $"{path}.{name}");
                }
            }
        }

        return null;
    }

    private static RoleRejection? OfArray(JsonElement schema, JsonElement value, string path)
    {
        if (!schema.TryGetProperty("items", out var items))
        {
            return null;
        }

        var index = 0;

        foreach (var item in value.EnumerateArray())
        {
            if (Violation(items, item, $"{path}[{index}]") is { } inner)
            {
                return inner;
            }

            index++;
        }

        return null;
    }

    /// <summary>Whether a value is of a type of a schema: one type, or one of a list.</summary>
    private static bool IsOf(JsonElement type, JsonElement value) => type.ValueKind == JsonValueKind.Array
        ? type.EnumerateArray().Any(one => IsOf(one.GetString()!, value))
        : IsOf(type.GetString()!, value);

    private static bool IsOf(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException($"A result schema names the type \"{type}\", which nothing checks. See docs/team.md."),
    };

    /// <summary>Whether a value is one that a schema lists: a text by its characters, and null as null.</summary>
    private static bool Same(JsonElement listed, JsonElement value) => listed.ValueKind switch
    {
        JsonValueKind.String => value.ValueKind == JsonValueKind.String && value.ValueEquals(listed.GetString()),
        JsonValueKind.Null => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException("A result schema lists a value that is neither a text nor null. See docs/team.md."),
    };

    private static RoleRejection Rejected(RejectionRule rule, string path) =>
        new(rule, path.Length == 0 ? RoleRejection.Answer : path);
}
