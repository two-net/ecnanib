using System.Text.Json;

namespace Ecnanib.Ai.Providers;

/// <summary>Null-tolerant readers for the raw model objects providers return (kept verbatim in <c>AiModelInfo.Capabilities</c>).</summary>
internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element, string name) =>
        TryGet(element, name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static int? GetInt32OrNull(this JsonElement element, string name) =>
        TryGet(element, name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) ? value : null;

    public static long? GetInt64OrNull(this JsonElement element, string name) =>
        TryGet(element, name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var value) ? value : null;

    public static JsonElement? GetObjectOrNull(this JsonElement element, string name) =>
        TryGet(element, name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement element, string name) =>
        TryGet(element, name, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static bool ArrayContainsString(this JsonElement element, string name, string value) =>
        element.EnumerateArrayOrEmpty(name).Any(e => e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), value, StringComparison.OrdinalIgnoreCase));

    private static bool TryGet(JsonElement element, string name, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out property))
            return true;
        property = default;
        return false;
    }
}
