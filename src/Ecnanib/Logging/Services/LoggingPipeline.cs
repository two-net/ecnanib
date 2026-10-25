using System.Globalization;
using Ecnanib.Logging.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Ecnanib.Logging.Services;

/// <summary>
/// The Serilog pipeline behind <c>ILogger&lt;T&gt;</c> (C-08, FR-LOG-03): one root at the lowest level either sink
/// wants, with the shared category levels as overrides so nothing neither sink wants is even created, and each sink
/// behind its own <see cref="SinkLevelFilter"/>.
/// </summary>
internal static class LoggingPipeline
{
    /// <summary><c>{Message:l}</c>: strings literal, every other value as its plain text, the way the entries read in the database too.</summary>
    internal const string ConsoleOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:l}{NewLine}{Exception}";

    /// <summary>The console sink as the application configures it: a timestamp with its offset, the level, the component and the message.</summary>
    public static void Console(LoggerSinkConfiguration sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        sinks.Console(outputTemplate: ConsoleOutputTemplate, formatProvider: CultureInfo.InvariantCulture);
    }

    public static LoggerConfiguration Configure(LoggerConfiguration configuration, LoggingOptions options, Action<LoggerSinkConfiguration> console, Action<LoggerSinkConfiguration> database)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(database);

        var consoleFilter = new SinkLevelFilter(options.Console.Minimum, options.LogLevel);
        var databaseFilter = new SinkLevelFilter(options.Database.Minimum, options.LogLevel);
        configuration.MinimumLevel.Is(consoleFilter.Minimum < databaseFilter.Minimum ? consoleFilter.Minimum : databaseFilter.Minimum);
        foreach (var (category, level) in options.LogLevel)
        {
            if (LoggingOptions.TryParseLevel(level) is { } parsed && !string.IsNullOrWhiteSpace(category))
                configuration.MinimumLevel.Override(category.Trim(), LevelConvert.ToSerilogLevel(parsed));
        }

        configuration.Enrich.FromLogContext();
        configuration.WriteTo.Conditional(consoleFilter.IsEnabled, console);
        configuration.WriteTo.Conditional(databaseFilter.IsEnabled, database);
        return configuration;
    }

    /// <summary>
    /// The console alone at Information, for a <c>Logging</c> section that fails validation: the logger is built
    /// inside the host's <c>Build()</c>, before the startup validation that reports the section, and the Critical
    /// message naming the failure must still print before the process exits (FR-CFG-06).
    /// </summary>
    public static LoggerConfiguration ConfigureFallback(LoggerConfiguration configuration, Action<LoggerSinkConfiguration> console)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(console);
        configuration.MinimumLevel.Is(LogEventLevel.Information);
        console(configuration.WriteTo);
        return configuration;
    }
}
