using System.Globalization;
using Ecnanib.Platform.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Builds where log events go, from the <c>Logging</c> section of the configuration file and from
/// nothing else (NFR-LOG-1, NFR-LOG-2). Events are structured: a message template with named
/// properties, a level, and the properties of the scopes they were written in.
/// </summary>
internal static class LogPipeline
{
    // One line per event: when, level, area, message, then the properties that the message does
    // not show, such as the correlation properties.
    private const string ConsoleLine =
        "{UtcTimestamp:yyyy-MM-dd HH:mm:ss.fff}Z [{Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// Creates the logger behind the host's logging. It applies no level of its own: the levels
    /// are <see cref="LogLevels"/>, which the host's logger factory enforces before an event
    /// gets here.
    /// </summary>
    /// <param name="settings">The outputs to write to. An output is on when its key is in the file.</param>
    /// <param name="contentRoot">The folder a relative log file path starts from.</param>
    /// <param name="mask">The masking every event passes before any output.</param>
    public static Logger Create(LoggingSettings settings, string contentRoot, SecretMask mask)
    {
        var outputs = new LoggerConfiguration().MinimumLevel.Verbose();

        if (settings.Outputs.Console)
        {
            outputs.WriteTo.Console(outputTemplate: ConsoleLine, formatProvider: CultureInfo.InvariantCulture);
        }

        if (settings.Outputs.File is { } file)
        {
            // One JSON object per line, so the named properties stay queryable. The file sink puts
            // the date before the extension and deletes the oldest files beyond the limit.
            outputs.WriteTo.File(
                new CompactJsonFormatter(),
                Path.GetFullPath(file.Path, contentRoot),
                rollingInterval: file.Rolling switch
                {
                    LogFileRolling.Day => RollingInterval.Day,
                    _ => throw new InvalidOperationException($"Rolling '{file.Rolling}' has no file interval."),
                },
                retainedFileCountLimit: file.RetainedFiles,

                // The sink's default stops writing for the rest of the day once a file reaches 1 GB.
                fileSizeLimitBytes: null);
        }

        return new LoggerConfiguration()
            .MinimumLevel.Verbose()

            // A secret that is part of a logged object prints as ***, like one logged on its own.
            .Destructure.AsScalar<Secret>()
            .WriteTo.Sink(new MaskingSink(outputs.CreateLogger(), mask))
            .CreateLogger();
    }
}
