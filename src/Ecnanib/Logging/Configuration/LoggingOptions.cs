using MelLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Ecnanib.Logging.Configuration;

/// <summary>
/// The <c>logging</c> section of the system configuration file (Appendix D; FR-LOG-03): the console's and the
/// database's minimum levels and the per-category levels both sinks share. <see cref="Console"/> and
/// <see cref="Database"/> give each sink its default; every <see cref="LogLevel"/> entry replaces that default for
/// the categories under its prefix (the longest prefix wins, a prefix matches a whole dotted segment). Levels are
/// bound as text so a misspelling is a validation failure, not a binding exception.
/// </summary>
public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    /// <summary>The accepted level names, from the most to the least verbose. <c>None</c> is not one: every event reaches both sinks (FR-LOG-01).</summary>
    public static readonly IReadOnlyList<string> LevelNames = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    /// <summary>Levels per category prefix, applied to both sinks in place of their minimum.</summary>
    public Dictionary<string, string> LogLevel { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public LogSinkOptions Console { get; set; } = new();

    public LogSinkOptions Database { get; set; } = new();

    /// <summary>A level by name, case-insensitively; numbers, <c>None</c> and unknown names are null.</summary>
    public static MelLevel? TryParseLevel(string? value)
    {
        foreach (var name in LevelNames)
        {
            if (string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<MelLevel>(name);
        }

        return null;
    }
}

/// <summary>One sink's settings: its minimum level, the default for every category the shared levels do not name.</summary>
public sealed class LogSinkOptions
{
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Not supported: a per-sink level table would bypass the shared one. Bound only so the validator can refuse it.</summary>
    public Dictionary<string, string>? LogLevel { get; set; }

    /// <summary>The parsed <see cref="MinimumLevel"/>; Information when the text is not a level name (validation refuses the file before that matters).</summary>
    public MelLevel Minimum => LoggingOptions.TryParseLevel(MinimumLevel) ?? MelLevel.Information;
}
