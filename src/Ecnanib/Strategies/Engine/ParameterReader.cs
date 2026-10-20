using System.Text.Json;

namespace Ecnanib.Strategies.Engine;

/// <summary>Typed access to the resolved parameters; a missing or mistyped one is a programming error (the descriptor must declare every name the strategy reads).</summary>
internal sealed class ParameterReader
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public ParameterReader(IReadOnlyDictionary<string, JsonElement> values)
    {
        _values = values;
    }

    public int Int(string name) => Get(name).TryGetInt32(out var value) ? value : throw Mistyped(name, "a whole number");

    public decimal Dec(string name) => Get(name).TryGetDecimal(out var value) ? value : throw Mistyped(name, "a number");

    public bool Flag(string name) => Get(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Mistyped(name, "true or false"),
    };

    public string Choice(string name) => Get(name).ValueKind == JsonValueKind.String ? Get(name).GetString()! : throw Mistyped(name, "a choice");

    private JsonElement Get(string name) =>
        _values.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"Parameter '{name}' is not declared by the strategy descriptor.");

    private static InvalidOperationException Mistyped(string name, string expected) =>
        new($"Parameter '{name}' is not {expected}.");
}
