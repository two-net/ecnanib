using Ecnanib.Logging.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using MelLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Ecnanib.Logging.Services;

/// <summary>
/// One sink's view of the levels (FR-LOG-03): the sink's minimum is its default, and a category level from the
/// shared table replaces it for every category under that prefix, the longest prefix winning. A prefix matches a
/// category exactly or at a dot, as Serilog's own overrides do, so <c>Binance</c> covers <c>Binance.RestClient</c>
/// and not <c>BinanceX</c>.
/// </summary>
internal sealed class SinkLevelFilter
{
    private readonly (string Prefix, LogEventLevel Level)[] _overrides;

    public SinkLevelFilter(MelLevel minimum, IReadOnlyDictionary<string, string> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        Minimum = LevelConvert.ToSerilogLevel(minimum);
        _overrides = levels
            .Select(pair => (pair.Key.Trim(), LoggingOptions.TryParseLevel(pair.Value)))
            .Where(pair => pair.Item1.Length > 0 && pair.Item2 is not null)
            .Select(pair => (pair.Item1, LevelConvert.ToSerilogLevel(pair.Item2!.Value)))
            .OrderByDescending(pair => pair.Item1.Length)
            .ToArray();
    }

    public LogEventLevel Minimum { get; }

    /// <summary>The level that applies to a category: the longest matching prefix's, else the sink's minimum.</summary>
    public LogEventLevel EffectiveLevel(string? category)
    {
        if (category is not null)
        {
            foreach (var (prefix, level) in _overrides)
            {
                if (Matches(prefix, category))
                    return level;
            }
        }

        return Minimum;
    }

    public bool IsEnabled(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return logEvent.Level >= EffectiveLevel(SourceContextOf(logEvent));
    }

    internal static bool Matches(string prefix, string category) =>
        category.StartsWith(prefix, StringComparison.Ordinal) && (category.Length == prefix.Length || category[prefix.Length] == '.');

    /// <summary>The <c>SourceContext</c> property, which the <c>ILogger&lt;T&gt;</c> bridge sets to the category.</summary>
    internal static string? SourceContextOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value) && value is ScalarValue { Value: string context } ? context : null;
}
