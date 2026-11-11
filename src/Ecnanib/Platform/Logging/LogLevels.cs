using Ecnanib.Platform.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// The log levels in force: the minimum level and the per-area overrides from the configuration
/// file (NFR-LOG-2). They are the host logger's only filter rules, and the one part of the file
/// that takes effect without a restart (NFR-CFG-7).
/// </summary>
/// <remarks>
/// The <see cref="LoggingSettings"/> registered in the container is the file as it was at startup.
/// The levels here are the current ones.
/// </remarks>
internal sealed class LogLevels(LoggingSettings atStartup)
    : IPostConfigureOptions<LoggerFilterOptions>, IOptionsChangeTokenSource<LoggerFilterOptions>
{
    private readonly Lock _gate = new();

    private LogLevel _minimum = atStartup.MinimumLevel;
    private IReadOnlyDictionary<string, LogLevel> _overrides = atStartup.Overrides;
    private CancellationTokenSource _changed = new();

    string? IOptionsChangeTokenSource<LoggerFilterOptions>.Name => Options.DefaultName;

    /// <summary>
    /// Puts the levels of <paramref name="settings"/> in force for every logger, existing ones
    /// included. Returns false when they are the levels already in force.
    /// </summary>
    public bool Apply(LoggingSettings settings)
    {
        CancellationTokenSource previous;

        lock (_gate)
        {
            if (settings.MinimumLevel == _minimum && Same(settings.Overrides, _overrides))
            {
                return false;
            }

            _minimum = settings.MinimumLevel;
            _overrides = settings.Overrides;

            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        // The logger factory listens to this and reads the levels again.
        previous.Cancel();
        previous.Dispose();

        return true;
    }

    /// <summary>Makes the levels the only rules, after anything the host configured by default.</summary>
    public void PostConfigure(string? name, LoggerFilterOptions options)
    {
        lock (_gate)
        {
            options.Rules.Clear();
            options.MinLevel = _minimum;

            foreach (var (area, level) in _overrides)
            {
                options.Rules.Add(new LoggerFilterRule(providerName: null, categoryName: area, logLevel: level, filter: null));
            }
        }
    }

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_changed.Token);
        }
    }

    private static bool Same(IReadOnlyDictionary<string, LogLevel> one, IReadOnlyDictionary<string, LogLevel> other) =>
        one.Count == other.Count
        && one.All(entry => other.TryGetValue(entry.Key, out var level) && level == entry.Value);
}
