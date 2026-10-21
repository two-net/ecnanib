using System.Text.Json;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The JSON shapes two trade columns share across the venues and the readers: <c>trades.stop_history</c> is an array
/// of <c>{at, price, reason}</c> entries whose first entry is the initial stop (FR-EXP-02), and <c>trades.targets</c>
/// and <c>decisions.targets</c> are arrays of prices. One writer and one reader for each, so the paper simulator, the
/// live venue and the pages cannot drift apart.
/// </summary>
public static class TradeJson
{
    /// <summary>Every target price of a decision's or trade's targets array, in order; nothing when the element is not an array.</summary>
    public static IReadOnlyList<decimal> Targets(JsonElement? targets)
    {
        if (targets is not { ValueKind: JsonValueKind.Array } array)
            return [];
        var result = new List<decimal>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var value))
                result.Add(value);
        }

        return result;
    }

    /// <summary>The initial stop: the price of the first history entry.</summary>
    public static decimal? InitialStop(JsonElement history)
    {
        if (history.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in history.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("price", out var price) && price.TryGetDecimal(out var value))
                return value;
        }

        return null;
    }

    /// <summary>A stop history with its first entry.</summary>
    public static JsonElement StopHistory(DateTimeOffset at, decimal stop, string reason = "initial") =>
        JsonSerializer.SerializeToElement(new[] { new { at, price = stop, reason } });

    /// <summary>The history with one more entry.</summary>
    public static JsonElement AppendStop(JsonElement history, DateTimeOffset at, decimal price, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            if (history.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in history.EnumerateArray())
                    item.WriteTo(writer);
            }

            writer.WriteStartObject();
            writer.WriteString("at", at);
            writer.WriteNumber("price", price);
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
