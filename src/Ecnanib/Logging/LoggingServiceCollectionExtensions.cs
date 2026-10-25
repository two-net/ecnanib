using Ecnanib.Logging.Abstractions;
using Ecnanib.Logging.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Logging;

public static class LoggingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the log viewer's reader, <see cref="ILogReader"/>, over <c>log_entries</c> (FR-LOG-07, NFR-SEC-03).
    /// Named to stay clear of the framework's <c>AddLogging</c>. Requires <c>AddDatabase</c>; has no configuration
    /// section (the top-level <c>Logging</c> section is the framework's console configuration). Connects to nothing
    /// at registration.
    /// </summary>
    public static IServiceCollection AddLogReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.TryAddSingleton<ILogReader, LogReader>();
        return services;
    }
}
