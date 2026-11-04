using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecnanib.Modules.AiAdapters.Contracts;

namespace Ecnanib.Modules.AiAdapters;

/// <summary>
/// What the provider adapters share: relative requests with a JSON body, and lenient reads of the answers.
/// A read gives null, zero or nothing for a field that's missing or of another type, so each adapter decides
/// what it can't do without, and throws <see cref="JsonException"/> for that. No exception quotes a body.
/// </summary>
internal static class AdapterJson
{
    /// <summary>A GET of <paramref name="path"/>, which starts with a single /, so the configured base URL goes in front.</summary>
    public static HttpRequestMessage Get(string path) => new(HttpMethod.Get, new Uri(path, UriKind.Relative));

    /// <summary>A POST of <paramref name="body"/> as UTF-8 JSON to <paramref name="path"/>.</summary>
    public static HttpRequestMessage Post(string path, JsonObject body) =>
        new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    /// <summary>A copy of <paramref name="schema"/>, because a node can have only one parent.</summary>
    public static JsonNode Node(AiOutputSchema schema) => JsonNode.Parse(schema.Schema.GetRawText())!;

    /// <summary>The model's own earlier turn, which is text only.</summary>
    public static string Text(AiMessage message) => string.Concat(message.Content.OfType<AiText>().Select(part => part.Text));

    public static string Base64(AiImage image) => Convert.ToBase64String(image.Data.Span);

    public static string DataUrl(AiImage image) => $"data:{image.MediaType};base64,{Base64(image)}";

    /// <exception cref="JsonException">The body isn't JSON.</exception>
    public static JsonDocument Parse(byte[] body) => JsonDocument.Parse(body);

    /// <exception cref="JsonException"><paramref name="element"/> has no object or array called <paramref name="name"/>.</exception>
    public static JsonElement Required(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? value
            : throw new JsonException($"The answer has no {name}.");

    public static JsonElement? Object(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    public static IEnumerable<JsonElement> Array(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Array(this JsonElement? element, string name) =>
        element is { } value ? value.Array(name) : [];

    public static string? String(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? String(this JsonElement? element, string name) => element?.String(name);

    /// <summary>A whole number, or 0.</summary>
    public static long Long(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : 0;

    public static long Long(this JsonElement? element, string name) => element?.Long(name) ?? 0;

    public static int? Int(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    public static bool Bool(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.True };

    /// <summary>The strings in an array, skipping anything else.</summary>
    public static IEnumerable<string> Strings(this JsonElement element, string name) =>
        element.Array(name).Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!);

    /// <summary>Unix time in seconds.</summary>
    public static DateTimeOffset? UnixSeconds(this JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt64(out var seconds)
        && seconds > 0
        && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>An RFC 3339 time.</summary>
    public static DateTimeOffset? Time(this JsonElement element, string name) =>
        DateTimeOffset.TryParse(element.String(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
}
