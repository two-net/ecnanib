using System.Globalization;
using System.Text.Json;
using Ecnanib.Database.Entities;
using Ecnanib.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;

namespace Ecnanib.Logging.Services;

/// <summary>
/// One Serilog event as a <c>log_entries</c> row (FR-LOG-02). The category is the component, the message is rendered
/// as the console renders it (strings literal), and the properties named <c>UserId</c>, <c>AgentId</c>, <c>DecisionId</c> and <c>OrderId</c> go to
/// their columns whether they came from the message or from a <see cref="LogScope"/>; every other property is kept
/// as one flat JSON object in <c>context</c>. Text is bounded so a stray payload cannot bloat a row, and NUL
/// characters are removed because PostgreSQL text and jsonb refuse them, which would fail the whole batch.
/// </summary>
internal static class LogEventMapper
{
    internal const int MaxComponentLength = 200;

    internal const int MaxMessageLength = 4_000;

    internal const int MaxExceptionLength = 16_000;

    internal const int MaxValueLength = 500;

    internal const int MaxPropertyJsonLength = 2_000;

    internal const string FallbackComponent = "Serilog";

    private static readonly JsonValueFormatter Json = new(typeTagName: null);

    /// <summary>The message as the console shows it: strings literal, every other value as its plain text.</summary>
    private static readonly MessageTemplateTextFormatter Message = new("{Message:l}", CultureInfo.InvariantCulture);

    public static LogEntry Map(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var promoted = new HashSet<string>(StringComparer.Ordinal);
        return new LogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = LevelConvert.ToExtensionsLevel(logEvent.Level),
            Component = Bound(SinkLevelFilter.SourceContextOf(logEvent) ?? FallbackComponent, MaxComponentLength),
            Message = Bound(RenderMessage(logEvent), MaxMessageLength),
            Exception = logEvent.Exception is null ? null : Bound(logEvent.Exception.ToString(), MaxExceptionLength),
            UserId = Id(logEvent, LogProperties.UserId, promoted),
            AgentId = Id(logEvent, LogProperties.AgentId, promoted),
            DecisionId = Id(logEvent, LogProperties.DecisionId, promoted),
            OrderId = Id(logEvent, LogProperties.OrderId, promoted),
            Context = Context(logEvent, promoted),
        };
    }

    /// <summary>A row for an event the mapper could not handle: the template and the component, nothing that could fail again.</summary>
    public static LogEntry Fallback(LogEvent logEvent, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(failure);
        return new LogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = LevelConvert.ToExtensionsLevel(logEvent.Level),
            Component = Bound(SinkLevelFilter.SourceContextOf(logEvent) ?? FallbackComponent, MaxComponentLength),
            Message = Bound($"{logEvent.MessageTemplate.Text} (the event could not be mapped: {failure.Message})", MaxMessageLength),
        };
    }

    /// <summary>The Guid under <paramref name="name"/>, if there is one; a value that is not a Guid stays in the context.</summary>
    private static Guid? Id(LogEvent logEvent, string name, HashSet<string> promoted)
    {
        foreach (var (key, value) in logEvent.Properties)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            Guid? id = value switch
            {
                ScalarValue { Value: Guid guid } => guid,
                ScalarValue { Value: string text } when Guid.TryParse(text, out var parsed) => parsed,
                _ => null,
            };
            if (id is not null)
                promoted.Add(key);
            return id;
        }

        return null;
    }

    private static string RenderMessage(LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Message.Format(logEvent, writer);
        return writer.ToString();
    }

    private static JsonElement? Context(LogEvent logEvent, HashSet<string> promoted)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var any = false;
        writer.Write('{');
        foreach (var (key, value) in logEvent.Properties)
        {
            if (promoted.Contains(key) || string.Equals(key, Constants.SourceContextPropertyName, StringComparison.Ordinal))
                continue;
            if (any)
                writer.Write(',');
            any = true;
            JsonValueFormatter.WriteQuotedJsonString(Bound(key, MaxValueLength), writer);
            writer.Write(':');
            writer.Write(Render(value));
        }

        if (!any)
            return null;
        writer.Write('}');
        return JsonDocument.Parse(writer.ToString()).RootElement.Clone();
    }

    /// <summary>The property as JSON, with its strings bounded; a value too large even so is kept as bounded text.</summary>
    private static string Render(LogEventPropertyValue value)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Json.Format(Clean(value), writer);
        var json = writer.ToString();
        if (json.Length <= MaxPropertyJsonLength)
            return json;

        using var text = new StringWriter(CultureInfo.InvariantCulture);
        JsonValueFormatter.WriteQuotedJsonString(Bound(value.ToString(), MaxValueLength), text);
        return text.ToString();
    }

    /// <summary>The same value with every string bounded and free of NUL characters.</summary>
    private static LogEventPropertyValue Clean(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } scalar => text.Length <= MaxValueLength && !text.Contains('\0') ? scalar : new ScalarValue(Bound(text, MaxValueLength)),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(Clean)),
        StructureValue structure => new StructureValue(structure.Properties.Select(p => new LogEventProperty(Bound(p.Name, MaxValueLength), Clean(p.Value))), structure.TypeTag),
        DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(pair => new KeyValuePair<ScalarValue, LogEventPropertyValue>((ScalarValue)Clean(pair.Key), Clean(pair.Value)))),
        _ => value,
    };

    /// <summary>Removes NUL characters and cuts the text at the limit, marking the cut.</summary>
    internal static string Bound(string text, int maxLength)
    {
        if (text.Contains('\0'))
            text = text.Replace("\0", string.Empty, StringComparison.Ordinal);
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }
}
