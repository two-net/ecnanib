using Ecnanib.Database.Entities;
using Ecnanib.Logging.Services;

namespace Ecnanib.Tests.Support;

/// <summary>Stands in for the log table: keeps every batch, or refuses them all while <see cref="Failing"/> is set.</summary>
internal sealed class FakeLogEntryStore : ILogEntryStore
{
    private readonly List<IReadOnlyList<LogEntry>> _batches = [];
    private int _calls;

    /// <summary>While set, every write throws as a down database would.</summary>
    public volatile bool Failing;

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<IReadOnlyList<LogEntry>> Batches
    {
        get
        {
            lock (_batches)
            {
                return _batches.ToArray();
            }
        }
    }

    public IReadOnlyList<LogEntry> Entries => Batches.SelectMany(b => b).ToList();

    public Task WriteAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        cancellationToken.ThrowIfCancellationRequested();
        if (Failing)
            throw new InvalidOperationException("database down");
        lock (_batches)
        {
            _batches.Add(entries.ToArray());
        }

        return Task.CompletedTask;
    }
}
