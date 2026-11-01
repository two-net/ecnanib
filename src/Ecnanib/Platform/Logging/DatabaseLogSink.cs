using Ecnanib.Platform.Contracts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// Writes to the log_events table, so the code-first model owns its schema (DATA-R-05). It builds
/// its own context options rather than using the application's factory: with no logger factory, EF
/// can't log these writes back into this sink, and the logger doesn't come to depend on itself.
/// </summary>
internal sealed class DatabaseLogSink : IBatchedLogEventSink
{
    private readonly DbContextOptions<EcnanibDbContext> _options;

    public DatabaseLogSink(string? connectionString)
    {
        var builder = new DbContextOptionsBuilder<EcnanibDbContext>().UseLoggerFactory(NullLoggerFactory.Instance);
        builder.UsePostgres(connectionString);

        _options = builder.Options;
    }

    /// <summary>Exceptions propagate, because the batching sink handles retries.</summary>
    public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
    {
        await using var context = new EcnanibDbContext(_options);

        context.Set<LogRecord>().AddRange(batch.Select(LogRecord.From));

        await context.SaveChangesAsync();
    }
}
