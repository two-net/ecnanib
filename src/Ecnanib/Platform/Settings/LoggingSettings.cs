namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Logging</c> section: the minimum level, per-area overrides, and outputs (NFR-LOG-2).</summary>
/// <param name="MinimumLevel">The level below which events are dropped.</param>
/// <param name="Overrides">A different minimum level for an area, such as <c>Microsoft.AspNetCore</c>.</param>
/// <param name="Outputs">Where events are written.</param>
public sealed record LoggingSettings(
    LogLevel MinimumLevel,
    IReadOnlyDictionary<string, LogLevel> Overrides,
    LogOutputsSettings Outputs)
{
    internal static LoggingSettings Read(SettingsReader file)
    {
        const string overridesKey = "Logging:Overrides";
        const string fileKey = "Logging:Outputs:File";

        var minimumLevel = file.Choice<LogLevel>("Logging:MinimumLevel");

        var overrides = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in file.Map(overridesKey))
        {
            overrides[area] = file.Choice<LogLevel>(SettingsReader.Join(overridesKey, area));
        }

        var console = file.Has("Logging:Outputs:Console");
        var logFile = file.Has(fileKey)
            ? new LogFileSettings(
                file.Text($"{fileKey}:Path"),
                file.Choice<LogFileRolling>($"{fileKey}:Rolling"),
                file.WholeNumber($"{fileKey}:RetainedFiles"))
            : null;

        return new LoggingSettings(minimumLevel, overrides, new LogOutputsSettings(console, logFile));
    }
}

/// <summary>The log outputs. An output is on when its key is present.</summary>
public sealed record LogOutputsSettings(bool Console, LogFileSettings? File);

/// <summary>A rolling log file with a retention limit.</summary>
public sealed record LogFileSettings(string Path, LogFileRolling Rolling, int RetainedFiles);

/// <summary>How often a new log file is started.</summary>
public enum LogFileRolling
{
    Day,
}
