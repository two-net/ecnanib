using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// The one gate in front of every sink (FR-LOG-05). It runs after enrichment, so it also sees
/// scopes, and it rebuilds the event because a LogEvent's exception and template are read-only.
/// If this throws, the event is dropped rather than written unmasked.
/// </summary>
internal sealed class SanitizingSink(ILogEventSink inner, SecretRules rules) : ILogEventSink
{
    private const string Category = "SourceContext";

    /// <summary>ASP.NET adds this to every event in a request, and a reset link can carry its token in the path.</summary>
    private const string RequestPath = nameof(RequestPath);

    /// <summary>
    /// Categories whose content can't be scanned, so they're held at a level where they don't carry
    /// it, whatever the configuration says. This overrides FR-LOG-03 here on purpose.
    /// </summary>
    private static readonly (string Category, LogEventLevel Floor)[] Floors =
    [
        // Logs every hub invocation with its arguments at Debug, which is how Blazor form input
        // travels, so a typed password would otherwise reach the log.
        ("Microsoft.AspNetCore.SignalR", LogEventLevel.Information),

        // Logs JS interop arguments, and every URI a circuit navigates to, at Debug.
        ("Microsoft.AspNetCore.Components.Server", LogEventLevel.Information),

        // Logs raw paths and query strings. The Http events replace it.
        ("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning),

        // These log the raw path, or the whole redirect URL, at Debug. A token in a link would be there.
        ("Microsoft.AspNetCore.Routing", LogEventLevel.Information),
        ("Microsoft.AspNetCore.Components.Routing", LogEventLevel.Information),
        ("Microsoft.AspNetCore.HttpsPolicy", LogEventLevel.Information),
        ("Microsoft.AspNetCore.StaticAssets", LogEventLevel.Information),
        ("Microsoft.AspNetCore.StaticFiles", LogEventLevel.Information),

        // These log a raw X-Forwarded-For entry and a raw Origin at Debug, and a client chooses both.
        ("Microsoft.AspNetCore.HttpOverrides", LogEventLevel.Information),
        ("Microsoft.AspNetCore.WebSockets", LogEventLevel.Information),
    ];

    private static readonly MessageTemplateParser Parser = new();

    public void Emit(LogEvent logEvent)
    {
        if (!IsBelowFloor(logEvent))
        {
            inner.Emit(SanitizeEvent(logEvent));
        }
    }

    private static bool IsBelowFloor(LogEvent logEvent)
    {
        if (logEvent.Properties.GetValueOrDefault(Category) is not ScalarValue { Value: string category })
        {
            return false;
        }

        foreach (var (name, floor) in Floors)
        {
            if (logEvent.Level < floor
                && category.StartsWith(name, StringComparison.Ordinal)
                && (category.Length == name.Length || category[name.Length] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    private LogEvent SanitizeEvent(LogEvent logEvent)
    {
        var template = SanitizeTemplate(logEvent.MessageTemplate, out var maskedTokens);

        var properties = logEvent.Properties
            .Where(property => property.Key != RequestPath)
            .Select(property => new LogEventProperty(
                property.Key,
                maskedTokens.Contains(property.Key) ? Masked() : SanitizeNamed(property.Key, property.Value)));

        return new LogEvent(
            logEvent.Timestamp.ToUniversalTime(),
            logEvent.Level,
            SanitizeException(logEvent.Exception),
            template,
            properties,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
    }

    /// <summary>
    /// A rule can match across a token, as in "Authorization: {Value}". The token leaves the template,
    /// but its value would still reach the JSON and the table, so the caller has to mask it too.
    /// </summary>
    private MessageTemplate SanitizeTemplate(MessageTemplate template, out HashSet<string> maskedTokens)
    {
        var text = SanitizeText(template.Text);

        if (text == template.Text)
        {
            maskedTokens = [];
            return template;
        }

        var sanitized = Parser.Parse(text);
        maskedTokens = [.. PropertyNames(template).Except(PropertyNames(sanitized))];

        return sanitized;
    }

    private static IEnumerable<string> PropertyNames(MessageTemplate template) =>
        template.Tokens.OfType<PropertyToken>().Select(token => token.PropertyName);

    private LogEventPropertyValue SanitizeNamed(string name, LogEventPropertyValue value) =>
        rules.IsSensitiveName(name) && !IsTokenCount(name, value) ? Masked() : SanitizeValue(value);

    private LogEventPropertyValue SanitizeValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => SanitizeScalar(scalar),
        SequenceValue sequence => new SequenceValue(sequence.Elements.Select(SanitizeValue)),
        StructureValue structure => SanitizeStructure(structure),

        // Two keys can sanitize to the same text, and DictionaryValue throws on a duplicate.
        DictionaryValue dictionary => new DictionaryValue(
            dictionary.Elements.Select(SanitizeElement).DistinctBy(element => element.Key)),

        _ => Masked(),
    };

    private ScalarValue SanitizeScalar(ScalarValue scalar)
    {
        if (IsFreeOfText(scalar.Value))
        {
            return scalar;
        }

        // Everything else, such as a Uri, stays an object until a sink renders it.
        var text = Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        var sanitized = SanitizeText(text);

        return sanitized == text ? scalar : new ScalarValue(sanitized);
    }

    private StructureValue SanitizeStructure(StructureValue structure)
    {
        var sensitiveKey = structure.Properties.Any(property =>
            property.Name == "Key" && property.Value is ScalarValue { Value: string key } && rules.IsSensitiveName(key));

        return new StructureValue(
            structure.Properties.Select(property => new LogEventProperty(
                property.Name,
                sensitiveKey && property.Name == "Value" ? Masked() : SanitizeNamed(property.Name, property.Value))),
            structure.TypeTag);
    }

    private KeyValuePair<ScalarValue, LogEventPropertyValue> SanitizeElement(
        KeyValuePair<ScalarValue, LogEventPropertyValue> element)
    {
        var name = Convert.ToString(element.Key.Value, CultureInfo.InvariantCulture) ?? string.Empty;

        return new KeyValuePair<ScalarValue, LogEventPropertyValue>(
            SanitizeScalar(element.Key),
            SanitizeNamed(name, element.Value));
    }

    private Exception? SanitizeException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var text = exception.ToString();
        var sanitized = SanitizeText(text);

        return sanitized == text ? exception : new MaskedException(sanitized);
    }

    /// <summary>PostgreSQL rejects NUL, so one such value would fail its whole batch for the retry window.</summary>
    private string SanitizeText(string text) =>
        rules.Scrub(text).Replace("\0", string.Empty, StringComparison.Ordinal);

    private static bool IsTokenCount(string name, LogEventPropertyValue value) =>
        value is ScalarValue { Value: sbyte or byte or short or ushort or int or uint or long or ulong }
        && SecretRules.IsTokenCount(name);

    private static bool IsFreeOfText(object? value) =>
        value is null or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double
            or decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Guid or Enum;

    private static ScalarValue Masked() => new(SecretRules.Mask);
}

/// <summary>Stands in for an exception whose text contained a secret. Sinks render ToString().</summary>
internal sealed class MaskedException(string text) : Exception(text)
{
    public override string ToString() => Message;
}
