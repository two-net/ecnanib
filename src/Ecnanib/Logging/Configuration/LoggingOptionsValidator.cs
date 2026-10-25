using Ecnanib.Logging.Services;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Ecnanib.Logging.Configuration;

/// <summary>Validates the <c>Logging</c> section at startup so an invalid file refuses to start (FR-CFG-06).</summary>
public sealed class LoggingOptionsValidator : IValidateOptions<LoggingOptions>
{
    /// <summary>
    /// Category prefixes whose Debug output carries the request's API-key header and signature (FR-LOG-05): the
    /// Binance client's loggers are <c>Binance.RestClient</c>, <c>Binance.SocketClient</c> and
    /// <c>Binance.TokenManager</c>, and its library's is <c>CryptoExchange</c>. Each sink must keep them at
    /// Information or above.
    /// </summary>
    internal static readonly IReadOnlyList<string> GuardedCategories = ["Binance", "CryptoExchange"];

    public ValidateOptionsResult Validate(string? name, LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var prefix = LoggingOptions.SectionName;
        var names = string.Join(", ", LoggingOptions.LevelNames.SkipLast(1)) + " or " + LoggingOptions.LevelNames[^1];

        ValidateSink("Console", options.Console);
        ValidateSink("Database", options.Database);

        foreach (var (category, level) in options.LogLevel)
        {
            if (string.Equals(category, "Default", StringComparison.OrdinalIgnoreCase))
                failures.Add($"{prefix}:LogLevel:Default is not used; set {prefix}:Console:MinimumLevel and {prefix}:Database:MinimumLevel instead.");
            else if (category.Contains('*', StringComparison.Ordinal))
                failures.Add($"{prefix}:LogLevel:{category} must be a category prefix without wildcards.");
            if (LoggingOptions.TryParseLevel(level) is null)
                failures.Add($"{prefix}:LogLevel:{category} must be {names}.");
        }

        if (failures.Count == 0)
        {
            const string reason = "the exchange library's Debug output carries API keys and signatures (FR-LOG-05).";
            foreach (var (category, level) in options.LogLevel)
            {
                if (GuardedCategories.Any(guarded => SinkLevelFilter.Matches(guarded, category.Trim())) && LoggingOptions.TryParseLevel(level) < Microsoft.Extensions.Logging.LogLevel.Information)
                    failures.Add($"{prefix}:LogLevel:{category} must be Information or higher: {reason}");
            }

            foreach (var (sink, settings) in new[] { ("Console", options.Console), ("Database", options.Database) })
            {
                var filter = new SinkLevelFilter(settings.Minimum, options.LogLevel);
                foreach (var guarded in GuardedCategories)
                {
                    var effective = filter.EffectiveLevel(guarded);
                    if (effective < LogEventLevel.Information && !options.LogLevel.Keys.Any(key => SinkLevelFilter.Matches(key.Trim(), guarded)))
                        failures.Add($"{prefix}:LogLevel:{guarded} must be set to Information or higher while {prefix}:{sink}:MinimumLevel is {settings.Minimum}: {reason}");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void ValidateSink(string sink, LogSinkOptions settings)
        {
            if (LoggingOptions.TryParseLevel(settings.MinimumLevel) is null)
                failures.Add($"{prefix}:{sink}:MinimumLevel must be {names}.");
            if (settings.LogLevel is { Count: > 0 })
                failures.Add($"{prefix}:{sink}:LogLevel is not supported; the levels in {prefix}:LogLevel apply to every sink.");
        }
    }
}
