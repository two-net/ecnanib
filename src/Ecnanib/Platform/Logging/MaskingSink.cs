using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// The one place where secrets are masked (NFR-LOG-4). It stands in front of every output, so an
/// event reaches the console or the file only after its properties, its message text, and its
/// exception text have passed <see cref="SecretMask"/>.
/// </summary>
internal sealed class MaskingSink(ILogEventSink outputs, SecretMask mask) : ILogEventSink, IDisposable
{
    private static readonly ScalarValue MaskedValue = new(SecretMask.Masked);

    private static readonly MessageTemplateParser Templates = new();

    public void Emit(LogEvent logEvent) => outputs.Emit(Mask(logEvent));

    public void Dispose() => (outputs as IDisposable)?.Dispose();

    private LogEvent Mask(LogEvent logEvent)
    {
        var changed = false;

        var properties = new List<LogEventProperty>(logEvent.Properties.Count);
        foreach (var (name, value) in logEvent.Properties)
        {
            var masked = MaskNamed(name, value);
            changed |= !ReferenceEquals(masked, value);
            properties.Add(new LogEventProperty(name, masked));
        }

        // The text of a template is code, but a message built without one arrives here as text.
        var template = logEvent.MessageTemplate;
        var templateText = mask.Text(template.Text);
        if (!ReferenceEquals(templateText, template.Text))
        {
            template = Templates.Parse(templateText);
            changed = true;
        }

        var exception = logEvent.Exception;
        if (exception is not null)
        {
            var exceptionText = exception.ToString();
            var maskedText = mask.Text(exceptionText);
            if (!ReferenceEquals(maskedText, exceptionText))
            {
                exception = new MaskedException(maskedText);
                changed = true;
            }
        }

        return changed
            ? new LogEvent(
                logEvent.Timestamp, logEvent.Level, exception, template, properties,
                logEvent.TraceId ?? default, logEvent.SpanId ?? default)
            : logEvent;
    }

    private LogEventPropertyValue MaskNamed(string name, LogEventPropertyValue value)
    {
        if (!mask.IsSensitiveName(name))
        {
            return MaskValue(value);
        }

        // Nothing was logged under the name, which is worth knowing and discloses nothing.
        return value is ScalarValue { Value: null } ? value : MaskedValue;
    }

    private LogEventPropertyValue MaskValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                return MaskText(value, text);

            // An address can carry a signature or a key in its query.
            case ScalarValue { Value: Uri address }:
                return MaskText(value, address.ToString());

            case SequenceValue sequence:
            {
                var elements = sequence.Elements.Select(MaskValue).ToList();
                return SameAs(elements, sequence.Elements) ? value : new SequenceValue(elements);
            }

            case StructureValue structure:
            {
                var members = structure.Properties
                    .Select(member => (member, masked: MaskNamed(member.Name, member.Value)))
                    .ToList();

                return members.All(pair => ReferenceEquals(pair.masked, pair.member.Value))
                    ? value
                    : new StructureValue(
                        members.Select(pair => new LogEventProperty(pair.member.Name, pair.masked)),
                        structure.TypeTag);
            }

            case DictionaryValue dictionary:
            {
                var entries = dictionary.Elements
                    .Select(entry => (entry, masked: entry.Key.Value is string name
                        ? MaskNamed(name, entry.Value)
                        : MaskValue(entry.Value)))
                    .ToList();

                return entries.All(pair => ReferenceEquals(pair.masked, pair.entry.Value))
                    ? value
                    : new DictionaryValue(entries.Select(
                        pair => new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.entry.Key, pair.masked)));
            }

            default:
                return value;
        }
    }

    private LogEventPropertyValue MaskText(LogEventPropertyValue value, string text)
    {
        var masked = mask.Text(text);

        return ReferenceEquals(masked, text) ? value : new ScalarValue(masked);
    }

    private static bool SameAs(List<LogEventPropertyValue> masked, IReadOnlyList<LogEventPropertyValue> original)
    {
        for (var index = 0; index < masked.Count; index++)
        {
            if (!ReferenceEquals(masked[index], original[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Stands for an exception whose text held a secret. Outputs print an exception with ToString.</summary>
    private sealed class MaskedException(string text) : Exception(text)
    {
        public override string ToString() => Message;
    }
}
