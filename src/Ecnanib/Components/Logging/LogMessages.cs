using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Components.Logging;

/// <summary>User-facing texts of the log viewer (FR-LOG-07).</summary>
internal static class LogMessages
{
    public const string NoEntries = "No log entries for this agent yet.";

    public const string NoMatches = "No log entries match the filter.";

    public const string FilterHint = "Times are UTC. The text filter matches the message, case-insensitively; the level filter shows that level and above.";

    public const string AllLevels = "All levels";

    public const string AllComponents = "All components";

    public static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => level.ToString(),
    };

    public static string LevelClass(LogLevel level) => "level-" + level.ToString().ToLowerInvariant();

    /// <summary>The last segment of a logger category, the type name; the full category goes in the title.</summary>
    public static string Component(string component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var dot = component.LastIndexOf('.');
        return dot < 0 || dot == component.Length - 1 ? component : component[(dot + 1)..];
    }

    public static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
