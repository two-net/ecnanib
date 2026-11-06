using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>How the trace's JSON columns are written: camel case, with enums by name, so a renamed member never reads as another.</summary>
internal static class TraceJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
