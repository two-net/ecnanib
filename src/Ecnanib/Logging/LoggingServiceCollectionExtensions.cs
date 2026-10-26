using Ecnanib.Logging.Abstractions;
using Ecnanib.Logging.Configuration;
using Ecnanib.Logging.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;

namespace Ecnanib.Logging;

public static class LoggingServiceCollectionExtensions
{
    /// <summary>
    /// Registers both log sinks (C-08, FR-LOG-01, FR-LOG-03, FR-LOG-08): Serilog as the <c>ILogger&lt;T&gt;</c>
    /// implementation with the console and the database sink behind the validated <see cref="LoggingOptions"/>
    /// levels, and the hosted writer that drains the database sink into <c>log_entries</c>. Register it before every
    /// module with a hosted service, so the writer stops last and still writes their shutdown logs. Requires
    /// <c>AddDatabase</c>; connects to nothing at registration.
    /// </summary>
    public static IServiceCollection AddLogSinks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LoggingOptions>()
            .Bind(configuration.GetSection(LoggingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LoggingOptions>, LoggingOptionsValidator>());

        // C-08: the console and the database sink are the only log outputs, so the providers the default host builder
        // registers (console, debug, event source) are removed; Serilog below is the one logger factory.
        services.AddLogging(logging => logging.ClearProviders());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILogEntryStore, DatabaseLogEntryStore>();
        services.TryAddSingleton<DatabaseLogSink>();
        services.TryAddSingleton<DatabaseLogWriter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DatabaseLogWriter>(sp => sp.GetRequiredService<DatabaseLogWriter>()));

        services.AddSerilog((provider, logger) =>
        {
            var sink = provider.GetRequiredService<DatabaseLogSink>();
            LoggingOptions options;
            try
            {
                options = provider.GetRequiredService<IOptions<LoggingOptions>>().Value;
            }
            catch (OptionsValidationException)
            {
                LoggingPipeline.ConfigureFallback(logger, LoggingPipeline.Console);
                return;
            }

            LoggingPipeline.Configure(logger, options, LoggingPipeline.Console, sinks => sinks.Sink(sink));
        });

        return services;
    }

    /// <summary>
    /// Registers the log viewer's reader, <see cref="ILogReader"/>, over <c>log_entries</c> (FR-LOG-07, NFR-SEC-03).
    /// Named to stay clear of the framework's <c>AddLogging</c>. Requires <c>AddDatabase</c>; has no configuration
    /// section of its own (the <c>Logging</c> section belongs to <see cref="AddLogSinks"/>). Connects to nothing at
    /// registration.
    /// </summary>
    public static IServiceCollection AddLogReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.TryAddSingleton<ILogReader, LogReader>();
        return services;
    }
}
