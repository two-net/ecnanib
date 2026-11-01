using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Primitives;
using Serilog;
using Serilog.Configuration;
using Serilog.Debugging;
using Serilog.Extensions.Hosting;
using Serilog.Formatting.Json;
using Serilog.Sinks.SystemConsole.Themes;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Owns the application's one Serilog logger and reapplies the Logging section whenever it changes,
/// without a restart (FR-LOG-03, CFG-04). It's built from DI rather than in Program.cs because
/// WebApplicationFactory only applies test settings once the builder builds. See docs/architecture.md.
/// </summary>
internal sealed class LoggingPipeline : IDisposable
{
    private const string ConsoleTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff}Z [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private static readonly string Version =
        typeof(LoggingPipeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private readonly IConfiguration _configuration;
    private readonly string _contentRoot;
    private readonly Lock _lock = new();
    private readonly ReloadableLogger _logger;
    private readonly IDisposable _changes;

    private LoggingOptions _options;
    private SecretRules _rules;
    private bool _databaseReady;

    /// <exception cref="InvalidOperationException">The Logging section is invalid, which stops startup.</exception>
    public LoggingPipeline(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration;
        _contentRoot = environment.ContentRootPath;
        _options = LoggingOptions.Read(configuration);
        _rules = SecretRules.From(configuration);

        // Reports sinks that fail. It writes raw exception text, so it gets the same rules.
        SelfLog.Enable(message => Console.Error.WriteLine(Volatile.Read(ref _rules).Scrub(message)));

        _logger = Configure(new LoggerConfiguration()).CreateBootstrapLogger();
        _changes = ChangeToken.OnChange(configuration.GetReloadToken, Apply);
    }

    public Serilog.ILogger Logger => _logger;

    /// <summary>
    /// Starts the database sink. Migrations create its table, so before this it could only fail, and
    /// a start that fails never gets here.
    /// </summary>
    public void AttachDatabase()
    {
        lock (_lock)
        {
            _databaseReady = true;

            if (_options.Sinks.Database.Enabled)
            {
                _logger.Reload(Configure);
            }
        }
    }

    public void Dispose()
    {
        _changes.Dispose();
        _logger.Dispose();
    }

    /// <summary>
    /// Validates before reloading, because Reload disposes the old logger before it builds the new
    /// one: if building threw, the application would have no logging until it restarts. A deleted
    /// file arrives here too, as an empty section, and is refused like any other invalid edit.
    /// </summary>
    private void Apply()
    {
        lock (_lock)
        {
            try
            {
                var options = LoggingOptions.Read(_configuration);
                var rules = SecretRules.From(_configuration);

                _options = options;
                Volatile.Write(ref _rules, rules);
            }
            catch (Exception exception)
            {
                // Broad because this runs in the configuration system's change callback. The whole
                // exception goes in, because the binder names the setting in an inner one.
                _logger.ForContext<LoggingPipeline>().Error(
                    exception,
                    "Ignored a change to the Logging section, so the previous settings stay in effect");

                return;
            }

            _logger.Reload(Configure);
            _logger.ForContext<LoggingPipeline>().Information("Applied a change to the Logging section");
        }
    }

    /// <summary>
    /// Masking runs on the Async worker because every event takes the reloadable logger's lock, so
    /// callers should only enqueue under it. LoggingOptions.Read must reject whatever could throw here.
    /// </summary>
    private LoggerConfiguration Configure(LoggerConfiguration logger)
    {
        logger.MinimumLevel.Is(_options.MinimumLevel!.Value);

        foreach (var (category, level) in _options.Overrides)
        {
            logger.MinimumLevel.Override(category, level);
        }

        var rules = _rules;

        return logger
            .Destructure.With<SensitiveTypesPolicy>()
            .Enrich.WithProperty("ApplicationVersion", Version)
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .WriteTo.Async(worker => worker.Sink(
                LoggerSinkConfiguration.Wrap(sinks => new SanitizingSink(sinks, rules), WriteToConfiguredSinks)));
    }

    private void WriteToConfiguredSinks(LoggerSinkConfiguration writeTo)
    {
        var sinks = _options.Sinks;

        if (sinks.Console.Enabled)
        {
            writeTo.Console(
                outputTemplate: ConsoleTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                theme: ConsoleTheme.None);
        }

        if (sinks.File.Enabled)
        {
            writeTo.File(
                new JsonFormatter(renderMessage: true, formatProvider: CultureInfo.InvariantCulture),
                Path.GetFullPath(sinks.File.Path!, _contentRoot),
                fileSizeLimitBytes: sinks.File.FileSizeLimitBytes,
                rollingInterval: sinks.File.RollingInterval == LogRollingInterval.Day
                    ? RollingInterval.Day
                    : RollingInterval.Infinite,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: sinks.File.RetainedFileCount);
        }

        if (sinks.Database.Enabled && _databaseReady)
        {
            writeTo.Sink(
                new DatabaseLogSink(_configuration.GetConnectionString("Default")),
                new BatchingOptions { QueueLimit = 10_000 },
                sinks.Database.MinimumLevel);
        }
    }
}
