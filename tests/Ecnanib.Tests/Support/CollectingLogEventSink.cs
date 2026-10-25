using Serilog.Core;
using Serilog.Events;

namespace Ecnanib.Tests.Support;

/// <summary>A Serilog sink that keeps every event it receives.</summary>
public sealed class CollectingLogEventSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToArray();
            }
        }
    }

    public IEnumerable<string> Messages => Events.Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }
}
