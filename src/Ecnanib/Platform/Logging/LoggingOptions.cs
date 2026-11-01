using Serilog.Events;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// The Logging section of the configuration file, in the shape SRS 7.3 defines. Read() is the only
/// way in, so startup and hot reload apply the same rules (CFG-03).
/// </summary>
internal sealed class LoggingOptions
{
    private const string Section = "Logging";

    public LogEventLevel? MinimumLevel { get; init; }

    /// <summary>Logger name prefix to level. A longer prefix wins.</summary>
    public Dictionary<string, LogEventLevel> Overrides { get; init; } = [];

    public LogSinksOptions Sinks { get; init; } = new();

    /// <exception cref="InvalidOperationException">A setting is missing or invalid. The message names it.</exception>
    public static LoggingOptions Read(IConfiguration configuration)
    {
        // Unknown keys are errors because a typo, or the framework's Logging:LogLevel, would
        // otherwise be ignored and leave the operator believing a setting had taken effect.
        var options = configuration.GetSection(Section)
            .Get<LoggingOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new LoggingOptions();

        options.Validate();

        return options;
    }

    /// <summary>
    /// Anything that could make the logger's configuration throw has to be rejected here. See
    /// LoggingPipeline for why a throw there can't be recovered from.
    /// </summary>
    private void Validate()
    {
        Require(
            MinimumLevel is { } minimum && Enum.IsDefined(minimum),
            $"{Section}:MinimumLevel is required, and must be one of {string.Join(", ", Enum.GetNames<LogEventLevel>())}.");

        foreach (var (category, level) in Overrides)
        {
            Require(!string.IsNullOrWhiteSpace(category), $"{Section}:Overrides has an empty category.");
            Require(Enum.IsDefined(level), $"{Section}:Overrides:{category} is not a valid level.");
        }

        Require(
            Enum.IsDefined(Sinks.Database.MinimumLevel),
            $"{Section}:Sinks:Database:MinimumLevel is not a valid level.");

        if (Sinks.File.Enabled)
        {
            ValidateFile(Sinks.File);
        }
    }

    private static void ValidateFile(LogFileOptions file)
    {
        var prefix = $"{Section}:Sinks:File";

        Require(!string.IsNullOrWhiteSpace(file.Path), $"{prefix}:Path is required when the file sink is enabled.");
        Require(
            file.Path!.IndexOfAny(Path.GetInvalidPathChars()) < 0,
            $"{prefix}:Path contains a character that isn't valid in a path.");
        Require(file.RetainedFileCount is > 0, $"{prefix}:RetainedFileCount is required, and must be at least 1.");
        Require(Enum.IsDefined(file.RollingInterval), $"{prefix}:RollingInterval must be Day or Size.");
        Require(file.FileSizeLimitBytes > 0, $"{prefix}:FileSizeLimitBytes must be at least 1.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class LogSinksOptions
{
    public LogConsoleOptions Console { get; init; } = new();

    public LogFileOptions File { get; init; } = new();

    public LogDatabaseOptions Database { get; init; } = new();
}

internal sealed class LogConsoleOptions
{
    public bool Enabled { get; init; } = true;
}

internal sealed class LogFileOptions
{
    public bool Enabled { get; init; }

    /// <summary>Relative paths start at the content root.</summary>
    public string? Path { get; init; }

    public LogRollingInterval RollingInterval { get; init; } = LogRollingInterval.Day;

    /// <summary>The size a file rolls at, whichever interval is set, so a large day never loses events.</summary>
    public long FileSizeLimitBytes { get; init; } = 1L << 30;

    public int? RetainedFileCount { get; init; }
}

internal sealed class LogDatabaseOptions
{
    public bool Enabled { get; init; }

    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Warning;
}

/// <summary>The two ways FR-LOG-02 allows a file to roll.</summary>
internal enum LogRollingInterval
{
    Day,
    Size,
}
