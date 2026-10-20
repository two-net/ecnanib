using System.Text.Json;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The strategy catalog of Appendix A, filtered by timeframe (FR-AGT-03). Every entry carries a version that agents
/// pin at creation (FR-STR-06); <see cref="All"/> and <see cref="ForTimeframe"/> list the newest version of each
/// strategy, <see cref="Find(string?, int)"/> answers the pinned one. Provided by the Strategies module
/// (<c>AddStrategies</c>, registered before <c>AddAgents</c>).
/// </summary>
public interface IStrategyCatalog
{
    /// <summary>The newest version of every strategy.</summary>
    IReadOnlyList<StrategyDescriptor> All { get; }

    /// <summary>Newest versions only; at least one strategy per agent timeframe (FR-AGT-03); empty for the weekly interval.</summary>
    IReadOnlyList<StrategyDescriptor> ForTimeframe(CandleInterval timeframe);

    /// <summary>The newest version of a strategy, or null.</summary>
    StrategyDescriptor? Find(string? id);

    /// <summary>Exactly that version of a strategy (FR-STR-06), or null when the module no longer ships it.</summary>
    StrategyDescriptor? Find(string? id, int version);
}

public enum StrategyParameterKind
{
    Integer,
    Decimal,
    Choice,
    Flag,
}

/// <summary>One editable strategy parameter with its default and bounds (FR-STR-04).</summary>
public sealed record StrategyParameter(
    string Name,
    string Label,
    StrategyParameterKind Kind,
    JsonElement Default,
    decimal? Min = null,
    decimal? Max = null,
    IReadOnlyList<string>? Choices = null)
{
    public static StrategyParameter Integer(string name, string label, int @default, int min, int max) =>
        new(name, label, StrategyParameterKind.Integer, JsonSerializer.SerializeToElement(@default), min, max);

    public static StrategyParameter Decimal(string name, string label, decimal @default, decimal min, decimal max) =>
        new(name, label, StrategyParameterKind.Decimal, JsonSerializer.SerializeToElement(@default), min, max);

    public static StrategyParameter Choice(string name, string label, string @default, params string[] choices) =>
        new(name, label, StrategyParameterKind.Choice, JsonSerializer.SerializeToElement(@default), Choices: choices);

    public static StrategyParameter Flag(string name, string label, bool @default) =>
        new(name, label, StrategyParameterKind.Flag, JsonSerializer.SerializeToElement(@default));

    public string DefaultText => Format(Default);

    /// <summary>The text a form shows for a value: numbers as written, choices bare, flags as true/false.</summary>
    public static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        _ => value.GetRawText(),
    };

    /// <summary>Parses what a user typed; blank means the default.</summary>
    public bool TryParse(string? text, out JsonElement value, out string? error)
    {
        error = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            value = Default;
            return true;
        }

        switch (Kind)
        {
            case StrategyParameterKind.Integer:
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
                {
                    value = default;
                    error = $"{Label} must be a whole number.";
                    return false;
                }

                value = JsonSerializer.SerializeToElement(i);
                break;
            case StrategyParameterKind.Decimal:
                if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    value = default;
                    error = $"{Label} must be a number.";
                    return false;
                }

                value = JsonSerializer.SerializeToElement(d);
                break;
            case StrategyParameterKind.Choice:
                value = JsonSerializer.SerializeToElement(text);
                break;
            case StrategyParameterKind.Flag:
                if (!bool.TryParse(text, out var b))
                {
                    value = default;
                    error = $"{Label} must be true or false.";
                    return false;
                }

                value = JsonSerializer.SerializeToElement(b);
                break;
            default:
                value = default;
                error = $"{Label} has an unknown kind.";
                return false;
        }

        error = Validate(value);
        return error is null;
    }

    /// <summary>Null when the value is of the right kind and within bounds.</summary>
    public string? Validate(JsonElement value)
    {
        switch (Kind)
        {
            case StrategyParameterKind.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var i))
                    return $"{Label} must be a whole number.";
                return i < Min || i > Max ? $"{Label} must be between {Min} and {Max}." : null;
            case StrategyParameterKind.Decimal:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var d))
                    return $"{Label} must be a number.";
                return d < Min || d > Max ? $"{Label} must be between {Min} and {Max}." : null;
            case StrategyParameterKind.Choice:
                return value.ValueKind == JsonValueKind.String && Choices!.Contains(value.GetString(), StringComparer.Ordinal)
                    ? null
                    : $"{Label} must be one of {string.Join(", ", Choices!)}.";
            case StrategyParameterKind.Flag:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"{Label} must be true or false.";
            default:
                return $"{Label} has an unknown kind.";
        }
    }
}

/// <summary>
/// A catalog entry (Appendix A) at one version. <see cref="Parameters"/> are the editable, bounded parameters with their
/// defaults (FR-STR-04); <see cref="HigherTimeframes"/> and <see cref="References"/> are the candle series beyond the
/// agent's own that the engine needs (FR-STR-03), which the market-data loader supplies in the <see cref="MarketSnapshot"/>.
/// </summary>
public sealed record StrategyDescriptor(
    string Id,
    string Name,
    CandleInterval Timeframe,
    int Version,
    string Summary,
    IReadOnlyList<StrategyParameter> Parameters,
    IReadOnlyList<CandleInterval> HigherTimeframes,
    IReadOnlyList<ReferenceSeries> References)
{
    public StrategyParameter? Parameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>The stored value or the default for every parameter, in catalog order.</summary>
    public IReadOnlyDictionary<string, JsonElement> Resolve(IReadOnlyDictionary<string, JsonElement> stored)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
            result[parameter.Name] = stored.TryGetValue(parameter.Name, out var value) ? value : parameter.Default;
        return result;
    }

    /// <summary>Kind, bounds and the cross-parameter rules; unknown names are errors so a typo never silently falls back to a default.</summary>
    public IReadOnlyList<FieldError> Validate(IReadOnlyDictionary<string, JsonElement> values)
    {
        var errors = new List<FieldError>();
        foreach (var (name, value) in values)
        {
            var parameter = Parameter(name);
            if (parameter is null)
            {
                errors.Add(new FieldError($"parameters.{name}", $"{name} is not a parameter of {Name}."));
                continue;
            }

            if (parameter.Validate(value) is { } error)
                errors.Add(new FieldError($"parameters.{name}", error));
        }

        if (errors.Count == 0)
        {
            var resolved = Resolve(values);
            if (resolved.TryGetValue(StrategyParameters.EmaFastPeriod, out var fast) && resolved.TryGetValue(StrategyParameters.EmaSlowPeriod, out var slow)
                && fast.TryGetInt32(out var f) && slow.TryGetInt32(out var s) && f >= s)
            {
                errors.Add(new FieldError($"parameters.{StrategyParameters.EmaFastPeriod}", "The fast EMA period must be shorter than the slow one."));
            }
        }

        return errors;
    }
}

/// <summary>Names of the parameters every strategy shares (Appendix A, "common parameters") and the JSON shape of <c>agents.parameters</c>.</summary>
public static class StrategyParameters
{
    public const string AtrPeriod = "atrPeriod";
    public const string EmaFastPeriod = "emaFastPeriod";
    public const string EmaSlowPeriod = "emaSlowPeriod";
    public const string StopBufferAtr = "stopBufferAtr";
    public const string PartialTakeProfitFraction = "partialTakeProfitFraction";
    public const string TrailingMethod = "trailingMethod";
    public const string EntryTimeoutCandles = "entryTimeoutCandles";
    public const string MarketFallback = "marketFallback";

    /// <summary>The upper bound on a stop's distance in ATR, a strategy-specific parameter the council's limits gate also reads (FR-EXE-04).</summary>
    public const string MaxStopDistanceAtr = "maxStopDistanceAtr";

    public const string TrailingBySwing = "swing";
    public const string TrailingByEma = "ema";

    /// <summary>Reads the flat object stored in <c>agents.parameters</c>; anything that is not an object reads as empty.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Read(JsonElement stored)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (stored.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in stored.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    /// <summary>Writes a flat object whose document outlives the entity (see <c>JsonValues</c>).</summary>
    public static JsonElement Write(IReadOnlyDictionary<string, JsonElement> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
