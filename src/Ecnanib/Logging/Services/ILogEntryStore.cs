using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Logging.Services;

/// <summary>Where the writer puts a batch of rows; the database in the application, a list in the tests.</summary>
internal interface ILogEntryStore
{
    Task WriteAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken);
}

/// <summary>One context per batch: the rows are inserted in one statement, and the retrying execution strategy covers transient failures.</summary>
internal sealed class DatabaseLogEntryStore : ILogEntryStore
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public DatabaseLogEntryStore(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task WriteAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.LogEntries.AddRange(entries);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
