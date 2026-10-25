using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Support;

/// <summary>Captures log entries, each with the scopes it was logged in, so tests can assert on what was (and was not) logged.</summary>
public sealed class TestLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly List<LogEntry> _entries = [];
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

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

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

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

    /// <summary><see cref="Scope"/> holds the key-value pairs of every scope the entry was logged in, an inner scope's value replacing an outer one's.</summary>
    public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Scope);

    private sealed class TestLogger(TestLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
            // Outermost first, so an inner scope's value wins.
            provider._scopes.ForEachScope((current, bag) =>
            {
                if (current is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var (key, value) in pairs)
                        bag[key] = value;
                }
            }, scope);
            provider.Add(new LogEntry(category, logLevel, formatter(state, exception), exception, scope));
        }
    }
}

public static class TestLogging
{
    public static ILoggerFactory CreateFactory(TestLoggerProvider provider) => LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
}
