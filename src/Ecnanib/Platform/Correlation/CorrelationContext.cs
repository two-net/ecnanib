using Ecnanib.Platform.Contracts;

namespace Ecnanib.Platform.Correlation;

/// <summary>
/// IDs are always generated here. One taken from a client could forge links between audit records,
/// and a long one could fail a batch of log rows on the indexed column. The ID stays a string,
/// because a Guid would reach log_events with dashes and stop matching the response header.
/// </summary>
internal sealed class CorrelationContext(ILoggerFactory loggerFactory) : ICorrelationContext
{
    private readonly AsyncLocal<string?> _current = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<CorrelationContext>();

    public string? CurrentId => _current.Value;

    public IDisposable Begin()
    {
        var outer = _current.Value;
        var id = Guid.CreateVersion7().ToString("N");

        _current.Value = id;

        return new Restore(
            _current,
            outer,
            _logger.BeginScope(new Dictionary<string, object?> { [LogProperties.CorrelationId] = id }));
    }

    private sealed class Restore(AsyncLocal<string?> current, string? outer, IDisposable? scope) : IDisposable
    {
        public void Dispose()
        {
            scope?.Dispose();
            current.Value = outer;
        }
    }
}
