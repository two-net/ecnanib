using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Support;

/// <summary>Captures log entries so tests can assert on what was (and was not) logged.</summary>
public sealed class TestLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public string AllText => string.Join("\n", Entries.Select(e => e.Message + " " + e.Exception));

    public ILogger CreateLogger(string categoryName) => new TestLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class TestLogger(TestLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}

public static class TestLogging
{
    public static ILoggerFactory CreateFactory(TestLoggerProvider provider) => LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
}
